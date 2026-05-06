namespace Verbara.Sdk.VoiceAi.TurnDetection.Internal;

internal sealed class MelFilterBank
{
    private readonly float[][] _filters;
    private readonly int _spectrumSize;

    public MelFilterBank(int nMels = 80, int nFft = 400, int sampleRate = 16000, float fMin = 0f, float fMax = 8000f)
    {
        _spectrumSize = nFft / 2 + 1;
        _filters = new float[nMels][];

        float melMin = HzToMel(fMin);
        float melMax = HzToMel(fMax);
        var melPoints = new float[nMels + 2];
        for (int i = 0; i < melPoints.Length; i++)
        {
            melPoints[i] = melMin + i * (melMax - melMin) / (nMels + 1);
        }

        var binIndices = new float[melPoints.Length];
        for (int i = 0; i < melPoints.Length; i++)
        {
            float hz = MelToHz(melPoints[i]);
            binIndices[i] = (nFft + 1) * hz / sampleRate;
        }

        for (int m = 0; m < nMels; m++)
        {
            _filters[m] = new float[_spectrumSize];
            float fLeft = binIndices[m];
            float fCenter = binIndices[m + 1];
            float fRight = binIndices[m + 2];

            float slaneyNorm = 2.0f / (MelToHz(melPoints[m + 2]) - MelToHz(melPoints[m]));

            for (int k = 0; k < _spectrumSize; k++)
            {
                if (k >= fLeft && k < fCenter)
                {
                    _filters[m][k] = slaneyNorm * (k - fLeft) / (fCenter - fLeft);
                }
                else if (k >= fCenter && k <= fRight)
                {
                    _filters[m][k] = slaneyNorm * (fRight - k) / (fRight - fCenter);
                }
            }
        }
    }

    public void Apply(ReadOnlySpan<float> powerSpectrum, Span<float> melEnergies)
    {
        for (int m = 0; m < _filters.Length; m++)
        {
            float sum = 0;
            var filter = _filters[m];
            for (int k = 0; k < _spectrumSize; k++)
            {
                sum += filter[k] * powerSpectrum[k];
            }

            melEnergies[m] = sum;
        }
    }

    private static float HzToMel(float hz) => 2595.0f * MathF.Log10(1.0f + hz / 700.0f);
    private static float MelToHz(float mel) => 700.0f * (MathF.Pow(10.0f, mel / 2595.0f) - 1.0f);
}
