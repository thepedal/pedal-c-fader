// Pedal C-fader — crossfader engine (no ReBuzz SDK dependency, testable headless).
//
// Behaviour reverse-engineered from kibibu Slice n Dice (Cameron Foale):
//   * gain of channel i = max(0, 1 - |pos - i|)   (triangular, adjacent pair sums to 1)
//   * on a Fade change, every channel gain ramps linearly from its current value to
//     the new target over Inertia ticks (SamplesPerTick * ticks samples);
//     Inertia 0 ("None") uses a fixed 32-sample anti-click ramp
//   * an Inertia change during a ramp rescales the samples still to go by
//     newTicks / oldTicks
//   * the very first target is applied instantly (no fade-in on load)
//
// Differences from the original:
//   * 2 to 6 channels instead of a fixed 3 (A, B, C); Fade spans A to the last
//     active channel, so 240 divides evenly for every channel count
//   * optional equal-power curve (sin of the linear gain)

using System;

namespace WDE.PedalCFader
{
    public sealed class FaderCore
    {
        public const int MaxChannels = 6;
        public const int MinChannels = 2;
        public const int FadeMax     = 240;
        public const int InertiaMax  = 240;
        public const int ZeroInertiaRampSamples = 32;   // Slice n Dice value

        readonly float[] _cur  = new float[MaxChannels];
        readonly float[] _tgt  = new float[MaxChannels];
        readonly float[] _step = new float[MaxChannels];

        int   _rampLeft;     // samples still to go in the current ramp
        float _ticks;        // current inertia in ticks
        bool  _first = true; // first target snaps instead of ramping

        public float Current(int ch) => _cur[ch];
        public float Target(int ch)  => _tgt[ch];
        public int   RampLeft        => _rampLeft;

        // ── Inertia value → ticks as a fraction (Slice n Dice table) ─────────────
        // 0..31    : v/32         (0, 1/32 .. 31/32)
        // 32..63   : (v-16)/16    (1 .. 2 15/16)
        // 64..95   : (v-40)/8     (3 .. 6 7/8)
        // 96..127  : (v-68)/4     (7 .. 14 3/4)
        // 128..172 : (v-98)/2     (15 .. 37)
        // 173..240 : v-136        (37 .. 104)
        public static void InertiaFraction(int v, out int num, out int den)
        {
            if (v < 32)       { num = v;       den = 32; }
            else if (v < 64)  { num = v - 16;  den = 16; }
            else if (v < 96)  { num = v - 40;  den = 8;  }
            else if (v < 128) { num = v - 68;  den = 4;  }
            else if (v < 173) { num = v - 98;  den = 2;  }
            else              { num = v - 136; den = 1;  }
        }

        public static float InertiaTicks(int v)
        {
            InertiaFraction(v, out int num, out int den);
            return num / (float)den;
        }

        public static string DescribeInertia(int v)
        {
            if (v <= 0) return "None";
            InertiaFraction(v, out int num, out int den);
            int whole = num / den, rem = num % den;
            if (rem != 0)
            {
                // reduce the fraction for display (the original prints it unreduced)
                int g = Gcd(rem, den); rem /= g; int d = den / g;
                return whole == 0 ? $"{rem}/{d} Tick" : $"{whole} {rem}/{d} Ticks";
            }
            return whole == 1 ? "1 Tick" : $"{whole} Ticks";
        }

        static int Gcd(int a, int b) { while (b != 0) { int t = a % b; a = b; b = t; } return a; }

        // ── Fade value → per-channel gains ───────────────────────────────────────
        public static void ComputeGains(int fade, int channels, bool equalPower, float[] dst)
        {
            channels = Math.Clamp(channels, MinChannels, MaxChannels);
            fade     = Math.Clamp(fade, 0, FadeMax);
            float pos = fade * (channels - 1) / (float)FadeMax;   // 0 .. channels-1
            for (int i = 0; i < MaxChannels; i++)
            {
                float g = 0f;
                if (i < channels)
                {
                    g = 1f - Math.Abs(pos - i);
                    if (g < 0f) g = 0f;
                    if (equalPower && g > 0f && g < 1f)
                        g = (float)Math.Sin(g * (Math.PI * 0.5));
                }
                dst[i] = g;
            }
        }

        public static string DescribeFade(int fade, int channels, bool equalPower)
        {
            var g = new float[MaxChannels];
            ComputeGains(fade, channels, equalPower, g);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < MaxChannels; i++)
            {
                if (g[i] <= 1e-5f) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append((char)('A' + i));
                if (g[i] < 0.99995f) sb.Append(' ').Append(Math.Round(g[i] * 100f)).Append('%');
            }
            return sb.Length > 0 ? sb.ToString() : "-";
        }

        // ── State changes (call from Work, Inertia before Fade, as the original) ─
        public void SetInertia(int v)
        {
            float t = InertiaTicks(v);
            if (_rampLeft > 0 && _ticks > 0f)
                _rampLeft = (int)Math.Round(t / _ticks * _rampLeft);
            _ticks = t;
        }

        public void SetTargets(int fade, int channels, bool equalPower, int samplesPerTick)
        {
            ComputeGains(fade, channels, equalPower, _tgt);
            if (_first)
            {
                Array.Copy(_tgt, _cur, MaxChannels);
                _first = false;
                _rampLeft = 0;
                return;
            }
            _rampLeft = _ticks <= 0f
                ? ZeroInertiaRampSamples
                : Math.Max(1, (int)Math.Round(samplesPerTick * (double)_ticks));
        }

        /// <summary>Sum the inputs into <paramref name="outL"/>/<paramref name="outR"/>
        /// with ramped gains. <paramref name="inL"/>[ch] may be null (pin unconnected).
        /// Returns true if any connected input had non-zero gain during the block.</summary>
        public bool Process(float[][] inL, float[][] inR, float[] outL, float[] outR, int n)
        {
            Array.Clear(outL, 0, n);
            Array.Clear(outR, 0, n);
            bool active = false;

            int rampN = Math.Min(n, _rampLeft);
            if (rampN > 0)
                for (int c = 0; c < MaxChannels; c++)
                    _step[c] = (_tgt[c] - _cur[c]) / _rampLeft;

            for (int c = 0; c < MaxChannels; c++)
            {
                float g = _cur[c];
                float[] l = inL[c], r = inR[c];
                bool silent = g == 0f && (rampN == 0 || _tgt[c] == 0f);

                if (l != null && !silent)
                {
                    active = true;
                    int s = 0;
                    if (rampN > 0)
                    {
                        float st = _step[c];
                        for (; s < rampN; s++)
                        {
                            outL[s] += g * l[s];
                            outR[s] += g * r[s];
                            g += st;
                        }
                    }
                    float gt = rampN > 0 && rampN == _rampLeft ? _tgt[c] : g;
                    if (gt != 0f)
                        for (; s < n; s++)
                        {
                            outL[s] += gt * l[s];
                            outR[s] += gt * r[s];
                        }
                }

                // advance state whether or not the pin is connected
                _cur[c] = (rampN > 0 && rampN == _rampLeft) ? _tgt[c]
                        : (rampN > 0 ? _cur[c] + _step[c] * rampN : _cur[c]);
            }

            _rampLeft -= rampN;
            return active;
        }
    }
}
