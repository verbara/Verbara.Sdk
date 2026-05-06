namespace Verbara.Sdk.VoiceAi.TurnDetection.Internal;

internal static class HannWindow
{
    private static readonly float[] Coefficients = Precompute(400);

    public static ReadOnlySpan<float> Values => Coefficients;

    private static float[] Precompute(int size)
    {
        var window = new float[size];
        for (int i = 0; i < size; i++)
        {
            window[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / (size - 1)));
        }

        return window;
    }
}
