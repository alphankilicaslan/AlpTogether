sampler2D input : register(s0);

// Parameters (passed from WPF)
float Brightness : register(c0);  // e.g. 0.00
float Contrast   : register(c1);  // e.g. 1.15
float Saturation : register(c2);  // e.g. 1.30
float Gamma      : register(c3);  // e.g. 0.95

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 color = tex2D(input, uv);

    // 1. Levels Expansion (TV 16-235 range to PC 0-255 range)
    // Expands crushed/foggy blacks (16) down to true black (0), and whites (235) up to true white (255)
    float3 c = (color.rgb - 0.0627) / 0.8588;
    c = saturate(c);

    // 2. Contrast boost
    c = ((c - 0.5) * Contrast) + 0.5;
    c = saturate(c);

    // 3. Brightness offset
    c += Brightness;
    c = saturate(c);

    // 4. Saturation / Vibrance boost (Enhance Pixar red, sky blue, gold metallic tones)
    float luma = dot(c, float3(0.299, 0.587, 0.114));
    c = lerp(float3(luma, luma, luma), c, Saturation);
    c = saturate(c);

    // 5. Gamma correction (removes milky haze)
    c = pow(c, Gamma);

    return float4(c, color.a);
}
