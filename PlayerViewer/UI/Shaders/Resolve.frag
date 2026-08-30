#version 330
precision highp float;

in vec2 TexCoords;
out vec4 FragColor;

uniform sampler2D uColorTex;
uniform int uFactor;
uniform int uKeepAlpha;

void main()
{
    vec2 texel = 1.0 / vec2(textureSize(uColorTex, 0));
    vec2 origin = TexCoords - float(uFactor) * 0.5 * texel;

    vec4 sum = vec4(0.0);
    vec3 covered = vec3(0.0);
    for (int y = 0; y < uFactor; y++)
    {
        for (int x = 0; x < uFactor; x++)
        {
            vec4 c = texture(uColorTex, origin + (vec2(x, y) + 0.5) * texel);
            sum += c;
            covered += c.rgb * c.a;
        }
    }

    float taps = float(uFactor * uFactor);
    vec3 color = sum.rgb / taps;

    if (uKeepAlpha == 1 && sum.a > 0.0)
        color = covered / sum.a;

    FragColor.rgb = pow(color, vec3(1.0 / 2.2));
    FragColor.a = uKeepAlpha == 1 ? sum.a / taps : 1.0;
}
