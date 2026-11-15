// Written by Analysis/train_model.py. Do not edit by hand: record more data and retrain instead.
namespace Hemianopia.Room
{
    /// <summary>
    /// The numbers behind MissModel: a linear model that guesses how long an object would go unnoticed.
    /// guess (seconds) = exp( sum of Weights[i] * (fact[i] - Mean[i]) / Std[i], plus Bias ) - 1, capped at 20.
    /// </summary>
    public static class MissModelWeights
    {
        public const string TrainedOn = "3000 imaginary objects from free-looking recordings";
        public const string TestedOn = "40 real surprises: tells missed from found 0.99 of the time";

        public static readonly string[] Features = { "into_blind_deg", "up_down_deg", "from_head_deg", "since_uncovered_s", "uncovered_share", "beyond_scan_deg", "head_speed_dps", "eyes_toward_blind_deg" };
        public static readonly float[] Mean = { 25.3231f, 8.67278f, 19.6846f, 4.27627f, 0.279813f, -15.2737f, 9.47446f, -5.63849f };
        public static readonly float[] Std = { 7.52564f, 5.72025f, 11.2138f, 4.0721f, 0.259504f, 26.7465f, 13.3999f, 9.53564f };
        public static readonly float[] Weights = { -0.0495993f, -0.00948489f, -0.118515f, 0.441523f, 0.0264436f, 0.70761f, -0.0362988f, -0.100228f };
        public const float Bias = 1.71828f;
    }
}
