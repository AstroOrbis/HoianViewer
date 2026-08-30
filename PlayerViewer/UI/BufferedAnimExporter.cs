using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PlayerViewer.UI
{
    /// <summary>
    /// Streaming animation exporter. Frames come off the pipeline already resolved to output
    /// size and are piped into ffmpeg as they are produced, so nothing is staged raw on disk.
    ///
    /// Without trim the crop is the whole frame and is known upfront, so capture feeds the final
    /// encoder directly and there is only one pass.
    ///
    /// With trim the crop is the union of every frame's content and is not known until capture
    /// ends, so it stays two passes. Pass 1 pipes frames into a lossless FFV1 intermediate while
    /// a running bounding box is expanded over non-zero-alpha pixels; pass 2 re-encodes that
    /// intermediate through a crop filter into the final file. The intermediate keeps alpha bit
    /// exact, so the result is what a single pass would have produced.
    ///
    /// The render is always transparent, which doubles as the alpha oracle for the crop, so one
    /// render pass serves every format. An opaque format composites the background into each
    /// frame on the writer thread, after the bounding box has seen the real alpha.
    /// </summary>
    public class BufferedAnimExporter : IDisposable
    {
        public bool IsCapturing { get; private set; }
        public string OutputPath { get; private set; }

        //Set on the worker threads and polled by the UI thread, so they are published through
        //volatile fields rather than plain auto-properties.
        public bool IsEncoding => _isEncoding;
        public int EncodeProgress => _encodeProgress;

        /// <summary>Frames the current phase will process, or 0 when it has no per-frame signal.</summary>
        public int EncodeTotal => _encodeTotal;

        /// <summary>Name of the phase the encode is in, for the progress bar label.</summary>
        public string EncodeStage => _encodeStage;

        /// <summary>First failure of the run, or null. The UI reports it once encoding ends.</summary>
        public string Error => _error;

        volatile bool _isEncoding;
        volatile int _encodeProgress,
            _encodeTotal;
        volatile string _encodeStage,
            _error;
        readonly object _errorLock = new object();

        //Ceilings that only exist to bound a wedged worker; a legitimate encode is never cut off.
        const int WriterJoinMs = 60000;
        const int WriterKillJoinMs = 5000;
        const int EncodeWaitMs = 30 * 60000;

        //One frame is being filled by the render loop, one is being written, the rest absorb
        //jitter. Allocated once per export because the frame size is fixed for its duration;
        //ArrayPool cannot serve these at all, its largest bucket being 1 MB, so renting per frame
        //would mean a fresh large object heap allocation every frame.
        const int RingFrames = 4;

        int _width,
            _height,
            _fps,
            _webpQuality,
            _marginPx;
        OutputFormat _format;
        byte[] _background;
        bool _trim;

        string _tempPath;
        BlockingCollection<byte[]> _free;
        BlockingCollection<byte[]> _writeQueue;
        Thread _writer;
        Thread _encoder;

        Process _pass1;
        Stream _pass1In;
        StderrTail _pass1Err;

        volatile int _framesPushed,
            _framesWritten;

        //Set when the writer could not be joined: it still owns the ring, the queue and the input
        //pipe, so none of it may be torn down.
        bool _writerAbandoned;

        //Content bounding box in buffer (bottom-up) coordinates; expanded by the writer and read
        //by the encoder, so every access goes through _bboxLock.
        int _minX,
            _minY,
            _maxX,
            _maxY;
        readonly object _bboxLock = new object();

        /// <summary>
        /// Starts ffmpeg and the writer thread. Width and height must be even: they become
        /// ffmpeg's -video_size and, under trim, bound a crop the yuv420 encoders also need even.
        /// <paramref name="background"/> is a full-frame straight-RGBA buffer to composite the
        /// scene over, or null to keep the alpha channel. <paramref name="marginPx"/> is the trim
        /// margin in output pixels.
        /// </summary>
        public bool StartCapture(
            int width,
            int height,
            int fps,
            string outputPath,
            bool trim,
            OutputFormat format,
            byte[] background,
            int webpQuality,
            int marginPx
        )
        {
            if (IsCapturing || IsEncoding)
                return false;

            _trim = trim;
            _width = width;
            _height = height;
            _fps = fps;
            _format = format;
            _background = background;
            _webpQuality = webpQuality;
            _marginPx = marginPx;
            OutputPath = outputPath;
            _error = null;
            _encodeStage = null;
            _encodeProgress = 0;
            _encodeTotal = 0;
            _framesPushed = 0;
            _framesWritten = 0;
            _writerAbandoned = false;
            _tempPath = null;
            _minX = width;
            _minY = height;
            _maxX = -1;
            _maxY = -1;
            _pass1Err = new StderrTail();

            try
            {
                string args = ExportUtil.RawInputArgs(width, height, fps);
                if (trim)
                {
                    string dir = Path.Combine(Path.GetTempPath(), "PlayerViewerExport");
                    Directory.CreateDirectory(dir);
                    _tempPath = Path.Combine(
                        dir,
                        $"anim_{Guid.NewGuid():N}{ExportUtil.IntermediateExt}"
                    );
                    //No vflip here: the intermediate stays in OpenGL row order, which is the order
                    //the bounding box is in, so pass 2 can crop with it and flip afterwards.
                    args += ExportUtil.IntermediateArgs(_tempPath);
                }
                else
                    args += "-vf vflip " + ExportUtil.CodecArgs(format, webpQuality, outputPath);

                _pass1 = StartFfmpeg(args, _pass1Err, pipeInput: true, trackProgress: false);
                _pass1In = _pass1.StandardInput.BaseStream;
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
                CleanupPass1();
                TryDeleteTemp();
                return false;
            }

            _free = new BlockingCollection<byte[]>(RingFrames);
            for (int i = 0; i < RingFrames; i++)
                _free.Add(new byte[width * height * 4]);
            _writeQueue = new BlockingCollection<byte[]>(RingFrames);
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "AnimExportWrite" };
            _writer.Start();
            IsCapturing = true;
            return true;
        }

        //Starts ffmpeg with stderr streaming into the given tail. trackProgress also redirects
        //stdout, where -progress writes the frame counter.
        Process StartFfmpeg(string args, StderrTail stderr, bool pipeInput, bool trackProgress)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExportUtil.ResolveFfmpeg(),
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardInput = pipeInput,
                RedirectStandardError = true,
                RedirectStandardOutput = trackProgress,
                CreateNoWindow = true,
            };
            var proc = Process.Start(psi);
            proc.ErrorDataReceived += (s, e) => stderr.Add(e.Data);
            proc.BeginErrorReadLine();
            if (trackProgress)
            {
                proc.OutputDataReceived += (s, e) => ReadProgress(e.Data);
                proc.BeginOutputReadLine();
            }
            return proc;
        }

        //-progress emits key=value lines; frame= is the one worth showing.
        void ReadProgress(string line)
        {
            if (line == null || !line.StartsWith("frame=", StringComparison.Ordinal))
                return;
            if (int.TryParse(line.AsSpan(6).Trim(), out int n))
                _encodeProgress = n;
        }

        /// <summary>
        /// Takes one frame buffer from the ring, blocking while the writer catches up, which is
        /// the backpressure that keeps the render loop from outrunning ffmpeg. Returns null once
        /// the export is over, and the caller then skips the frame.
        /// </summary>
        public byte[] RentFrameBuffer()
        {
            if (!IsCapturing)
                return null;
            try
            {
                if (_free.TryTake(out var buf, WriterJoinMs))
                    return buf;
                //The writer recycles a buffer even for a frame it could not write, so an empty
                //ring this long is a wedge. Skipping the frame silently would desync the clip.
                SetError("Frame writer stalled; export incomplete.");
            }
            catch { }
            return null;
        }

        /// <summary>Hands a filled bottom-up RGBA frame to the writer.</summary>
        public void PushFrame(byte[] rgba)
        {
            if (rgba == null)
                return;
            if (!IsCapturing)
            {
                Recycle(rgba);
                return;
            }
            try
            {
                _writeQueue.Add(rgba);
                _framesPushed++;
            }
            catch
            {
                Recycle(rgba);
            }
        }

        void Recycle(byte[] buf)
        {
            try
            {
                _free.Add(buf);
            }
            catch { }
        }

        void WriteLoop()
        {
            try
            {
                foreach (var buf in _writeQueue.GetConsumingEnumerable())
                {
                    //One failure ends the pass, but the loop keeps draining so the render loop
                    //always gets its buffer back instead of wedging on an empty ring.
                    if (_error == null)
                    {
                        try
                        {
                            if (_trim)
                                ScanBbox(buf);
                            if (_background != null)
                                Composite(buf);
                            _pass1In.Write(buf, 0, buf.Length);
                            _framesWritten++;
                        }
                        catch (Exception ex)
                        {
                            //A pipe write usually fails because ffmpeg already died (missing
                            //encoder, bad args). Let it settle so its stderr is in hand first.
                            SettleForStderr(_pass1);
                            Fail(ex.Message, _pass1Err);
                        }
                    }
                    Recycle(buf);
                }
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
            }
        }

        //Expands the bounding box to include every pixel with alpha != 0. Bails early once
        //the box already spans the whole frame (nothing left to trim). The scan runs on locals
        //and takes the lock once per frame instead of once per pixel.
        void ScanBbox(byte[] buf)
        {
            int w = _width,
                h = _height;
            int minX,
                minY,
                maxX,
                maxY;
            lock (_bboxLock)
            {
                if (_minX == 0 && _minY == 0 && _maxX == w - 1 && _maxY == h - 1)
                    return;
                minX = _minX;
                minY = _minY;
                maxX = _maxX;
                maxY = _maxY;
            }

            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    if (buf[row + x * 4 + 3] != 0)
                    {
                        if (x < minX)
                            minX = x;
                        if (x > maxX)
                            maxX = x;
                        if (y < minY)
                            minY = y;
                        if (y > maxY)
                            maxY = y;
                    }
                }
            }

            lock (_bboxLock)
            {
                _minX = minX;
                _minY = minY;
                _maxX = maxX;
                _maxY = maxY;
            }
        }

        //Source-over composite of the straight-alpha frame onto the background, in place. The
        //background is full frame with the same layout, so a pixel maps straight across. The
        //coverage weighting that keeps the matte colour out of edge pixels is already done by the
        //pipeline's resolve pass, so this is a plain composite.
        void Composite(byte[] frame)
        {
            var bg = _background;
            for (int i = 0; i + 3 < frame.Length; i += 4)
            {
                int a = frame[i + 3];
                if (a == 255)
                    continue;
                int ia = 255 - a;
                frame[i] = (byte)((frame[i] * a + bg[i] * ia) / 255);
                frame[i + 1] = (byte)((frame[i + 1] * a + bg[i + 1] * ia) / 255);
                frame[i + 2] = (byte)((frame[i + 2] * a + bg[i + 2] * ia) / 255);
                frame[i + 3] = 255;
            }
        }

        //First failure wins; later ones are noise from the same collapse.
        void SetError(string message)
        {
            if (string.IsNullOrEmpty(message))
                return;
            lock (_errorLock)
                _error ??= message;
        }

        /// <summary>
        /// Ends capture and finishes the encode on a worker thread. Returns immediately; poll
        /// <see cref="IsEncoding"/> and the progress fields.
        /// </summary>
        public void FinishCapture()
        {
            if (!IsCapturing)
                return;
            IsCapturing = false;

            _encodeStage = "Buffering";
            _encodeTotal = _framesPushed;
            _encodeProgress = _framesWritten;
            _isEncoding = true;
            _encoder = new Thread(EncodeLoop) { IsBackground = true, Name = "AnimExportEncode" };
            _encoder.Start();
        }

        //Closes the queue and waits for the writer to drain it. Joining is what publishes the
        //bounding box and the frames written so far to whatever runs next. Waits in slices so the
        //UI keeps seeing the drain advance.
        bool JoinWriter(int timeoutMs, bool track)
        {
            try
            {
                _writeQueue.CompleteAdding();
            }
            catch { }
            if (_writer == null)
                return true;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (_writer.Join(100))
                    return true;
                if (track)
                    _encodeProgress = _framesWritten;
            }
            return _writer.Join(0);
        }

        void EncodeLoop()
        {
            try
            {
                //A writer that will not join is almost always blocked writing to a pipe ffmpeg
                //stopped reading, so killing ffmpeg is what frees it.
                if (!JoinWriter(WriterJoinMs, track: true))
                {
                    KillQuietly(_pass1);
                    if (!JoinWriter(WriterKillJoinMs, track: false))
                    {
                        //The writer still owns the ring, the queue and the pipe, so tearing any of
                        //it down here would race it. Leave it all, intermediate included.
                        _writerAbandoned = true;
                        SetError("Frame writer did not finish; export abandoned.");
                        return;
                    }
                }
                _encodeProgress = _framesWritten;

                //Closing the pipe only tells ffmpeg the input ended; there is no frame counter for
                //what it does after that, so the phase reports no total.
                _encodeStage = _trim ? "Buffering" : "Encoding";
                _encodeTotal = 0;
                FinishFfmpeg(_pass1, _pass1In, _pass1Err);
                CleanupPass1();

                if (_trim && _error == null)
                    RunCropPass();
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
            }
            finally
            {
                if (!_writerAbandoned)
                    TryDeleteTemp();
                //Last, so a UI thread that sees encoding end also sees the error that ended it.
                _isEncoding = false;
            }
        }

        //Second pass: crop the intermediate to the content bounding box and encode the real
        //output. ffmpeg reads the file itself, so progress comes from its -progress counter.
        void RunCropPass()
        {
            ComputeCrop(out int x0, out int y0, out int cw, out int ch);

            _encodeStage = "Encoding";
            _encodeProgress = 0;
            //libwebp_anim emits the whole animation as one packet, so its frame counter goes
            //straight to 1 and says nothing; that phase reports no total and sweeps instead.
            _encodeTotal = _format == OutputFormat.WebpTransparent ? 0 : _framesWritten;

            var stderr = new StderrTail();
            Process proc = null;
            try
            {
                //The intermediate is still in OpenGL row order, so the bounding box is the crop
                //rect as it stands and the flip comes after it.
                string args =
                    ExportUtil.IntermediateInputArgs(_tempPath)
                    + $"-vf crop={cw}:{ch}:{x0}:{y0},vflip "
                    + ExportUtil.CodecArgs(_format, _webpQuality, OutputPath);
                proc = StartFfmpeg(args, stderr, pipeInput: false, trackProgress: true);
                FinishFfmpeg(proc, null, stderr);
            }
            catch (Exception ex)
            {
                SettleForStderr(proc);
                Fail(ex.Message, stderr);
            }
            finally
            {
                proc?.Dispose();
            }

            if (_error == null)
                _encodeProgress = _encodeTotal;
        }

        //Closes the input pipe, if there is one, and waits the process out. A lossless VP9 pass
        //can run for minutes after the last frame goes in, so wait it out rather than assume it
        //hung; a timeout is a reported failure, never a silent success.
        void FinishFfmpeg(Process proc, Stream stdin, StderrTail stderr)
        {
            if (proc == null)
                return;
            try
            {
                if (stdin != null)
                {
                    stdin.Flush();
                    stdin.Close();
                }
                if (!proc.WaitForExit(EncodeWaitMs))
                {
                    KillQuietly(proc);
                    Fail($"ffmpeg did not finish within {EncodeWaitMs / 60000} minutes", stderr);
                    return;
                }
                //The parameterless overload also waits for the async stderr reader to drain.
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                    Fail($"ffmpeg exited with code {proc.ExitCode}", stderr);
            }
            catch (Exception ex)
            {
                SettleForStderr(proc);
                Fail(ex.Message, stderr);
            }
        }

        //Crop rect in buffer (bottom-up) coordinates. An empty box means nothing was ever drawn,
        //so the whole frame is kept. The edges are rounded out to even numbers because the yuv420
        //encoders cannot take odd dimensions.
        void ComputeCrop(out int x0, out int y0, out int cw, out int ch)
        {
            int minX,
                minY,
                maxX,
                maxY;
            lock (_bboxLock)
            {
                minX = _minX;
                minY = _minY;
                maxX = _maxX;
                maxY = _maxY;
            }

            if (maxX < 0)
            {
                x0 = 0;
                y0 = 0;
                cw = _width;
                ch = _height;
                return;
            }

            x0 = Math.Max(0, minX - _marginPx) & ~1;
            y0 = Math.Max(0, minY - _marginPx) & ~1;
            cw = AlignCrop(Math.Min(_width - 1, maxX + _marginPx) - x0 + 1, x0, _width);
            ch = AlignCrop(Math.Min(_height - 1, maxY + _marginPx) - y0 + 1, y0, _height);
        }

        //Grows a crop edge out to an even size, then back in if that ran past the frame. The
        //origin is even, so the result stays even and never falls below one pixel pair.
        static int AlignCrop(int size, int origin, int limit)
        {
            size += size & 1;
            if (origin + size > limit)
                size = (limit - origin) & ~1;
            return Math.Max(size, 2);
        }

        //Reports a failure with ffmpeg's own output appended, which is what makes it diagnosable.
        void Fail(string message, StderrTail stderr)
        {
            string tail = stderr.ToString();
            SetError(tail.Length > 0 ? message + Environment.NewLine + tail : message);
        }

        static void SettleForStderr(Process proc)
        {
            if (proc == null)
                return;
            try
            {
                if (proc.WaitForExit(5000))
                    proc.WaitForExit();
                else
                    KillQuietly(proc);
            }
            catch { }
        }

        static void KillQuietly(Process proc)
        {
            if (proc == null)
                return;
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            catch { }
        }

        /// <summary>Aborts capture without finishing the encode (cancel button). Safe if idle.</summary>
        public void Abort()
        {
            if (!IsCapturing)
                return;
            IsCapturing = false;

            //None of this is going to be encoded, so kill ffmpeg first: it unblocks a writer that
            //is waiting on the pipe and makes the join immediate.
            KillQuietly(_pass1);
            if (!JoinWriter(WriterJoinMs, track: false))
            {
                //Same as the encode path: the writer still owns the ring, the queue and the pipe.
                _writerAbandoned = true;
                return;
            }
            CleanupPass1();
            TryDeleteTemp();
            //Without trim the killed pass was writing the real output, so what is on disk is a
            //truncated file at the path the user picked. ffmpeg's -y already replaced whatever was
            //there, so there is nothing to preserve by keeping it.
            if (!_trim)
                TryDelete(OutputPath);
        }

        void CleanupPass1()
        {
            try
            {
                _pass1In?.Dispose();
            }
            catch { }
            _pass1In = null;
            _pass1?.Dispose();
            _pass1 = null;
        }

        void TryDeleteTemp() => TryDelete(_tempPath);

        static void TryDelete(string path)
        {
            try
            {
                if (path != null && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        public void Dispose()
        {
            Abort();
            //Neither collection may be torn down while the writer is still consuming them.
            if (_writerAbandoned || (_writer != null && _writer.IsAlive))
                return;
            _writeQueue?.Dispose();
            _writeQueue = null;
            _free?.Dispose();
            _free = null;
            if (_encoder == null || !_encoder.IsAlive)
                TryDeleteTemp();
        }

        /// <summary>
        /// Bounded ring of ffmpeg's most recent stderr lines. ffmpeg opens with a banner and
        /// prints the reason it failed last, so the tail is the part worth keeping, and the
        /// caps mean a chatty or looping encoder cannot grow this without limit.
        /// </summary>
        class StderrTail
        {
            const int MaxLines = 40;
            const int MaxLineChars = 400;

            readonly Queue<string> _lines = new Queue<string>();

            public void Add(string line)
            {
                if (line == null)
                    return;
                lock (_lines)
                {
                    _lines.Enqueue(
                        line.Length > MaxLineChars ? line.Substring(0, MaxLineChars) : line
                    );
                    if (_lines.Count > MaxLines)
                        _lines.Dequeue();
                }
            }

            public override string ToString()
            {
                lock (_lines)
                    return string.Join(Environment.NewLine, _lines);
            }
        }
    }
}
