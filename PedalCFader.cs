// Pedal C-fader — a 2 to 6 channel crossfader for ReBuzz.
//
// Managed replica of kibibu Slice n Dice (Cameron Foale). The original takes up
// to 12 inputs, assigns each to channel A, B or C, and crossfades A -> B -> C
// with an Inertia (glide) measured in ticks. Here each channel is its own input
// pin (EffectBlockMulti), so assigning a source to a channel is done by choosing
// the pin when connecting; several sources on one pin are summed by ReBuzz.
//
// Pins: all 6 inputs are always declared. The Channels parameter only sets how
// many of them the fader spans. Changing the pin count at runtime would crash
// song reload for any connection saved on a pin above the new count
// (MachineCore.GetMultiIOSamples indexes activeChannels by DestinationChannel;
// same failure as Tracker §12.2 on the output side).

using System;
using System.Collections.Generic;
using System.Windows;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using BuzzGUI.Common;

namespace WDE.PedalCFader
{
    [MachineDecl(Name = "Pedal C-fader", ShortName = "C-fader", Author = "Pedal",
                 InputCount = FaderCore.MaxChannels, OutputCount = 1)]
    public class PedalCFaderMachine : IBuzzMachine, IDisposable
    {
        internal const string Version = "1.0.0";

        readonly IBuzzMachineHost host;
        readonly FaderCore core = new FaderCore();

        // ── Parameters (declaration order is the preset/save contract; append only) ──

        int _fade, _inertia = 16, _channels = 3, _curve;
        volatile bool _inertiaDirty = true, _targetsDirty = true;

        [ParameterDecl(Name = "Fade", MinValue = 0, MaxValue = FaderCore.FadeMax, DefValue = 0,
            Description = "Crossfade position. 0 is channel A, 240 is the last active channel")]
        public int Fade
        {
            get => _fade;
            set { _fade = Math.Clamp(value, 0, FaderCore.FadeMax); _targetsDirty = true; }
        }

        [ParameterDecl(Name = "Inertia", MinValue = 0, MaxValue = FaderCore.InertiaMax, DefValue = 16,
            Description = "Time taken to reach a new Fade position, in ticks")]
        public int Inertia
        {
            get => _inertia;
            set { _inertia = Math.Clamp(value, 0, FaderCore.InertiaMax); _inertiaDirty = true; }
        }

        [ParameterDecl(Name = "Channels", MinValue = FaderCore.MinChannels, MaxValue = FaderCore.MaxChannels,
            DefValue = 3, Description = "Number of input channels the fader spans, 2 to 6")]
        public int Channels
        {
            get => _channels;
            set { _channels = Math.Clamp(value, FaderCore.MinChannels, FaderCore.MaxChannels); _targetsDirty = true; }
        }

        [ParameterDecl(Name = "Curve", DefValue = 0,
            ValueDescriptions = new[] { "Linear", "Equal Power" },
            Description = "Linear matches Slice n Dice. Equal Power avoids the level dip mid-fade")]
        public int Curve
        {
            get => _curve;
            set { _curve = Math.Clamp(value, 0, 1); _targetsDirty = true; }
        }

        // ── Construction / channel setup ────────────────────────────────────────

        public PedalCFaderMachine(IBuzzMachineHost host)
        {
            this.host = host;
            try
            {
                var song = Global.Buzz?.Song;   // never host.Machine here (Build §8.1)
                if (song != null)
                {
                    song.MachineAdded += OnMachineAdded;
                    song.MachineRemoved += OnMachineRemoved;
                }
            }
            catch { }
        }

        void OnMachineAdded(IMachine m)
        {
            if (m != host.Machine) return;
            try
            {
                host.InputChannelCount = FaderCore.MaxChannels;
                host.OutputChannelCount = 1;
            }
            catch { }
        }

        void OnMachineRemoved(IMachine m)
        {
            if (m == host.Machine) Unsubscribe();
        }

        void Unsubscribe()
        {
            try
            {
                var song = Global.Buzz?.Song;
                if (song != null)
                {
                    song.MachineAdded -= OnMachineAdded;
                    song.MachineRemoved -= OnMachineRemoved;
                }
            }
            catch { }
        }

        public void Dispose() => Unsubscribe();

        // ── Audio ───────────────────────────────────────────────────────────────

        float[][] _inL = new float[FaderCore.MaxChannels][];
        float[][] _inR = new float[FaderCore.MaxChannels][];
        float[][] _bufL = new float[FaderCore.MaxChannels][];
        float[][] _bufR = new float[FaderCore.MaxChannels][];
        float[] _outL = new float[256], _outR = new float[256];

        void EnsureScratch(int n)
        {
            if (_outL.Length >= n && _bufL[0] != null && _bufL[0].Length >= n) return;
            int size = Math.Max(256, n);
            _outL = new float[size]; _outR = new float[size];
            for (int c = 0; c < FaderCore.MaxChannels; c++)
            {
                _bufL[c] = new float[size];
                _bufR[c] = new float[size];
            }
        }

        public bool Work(IList<Sample[]> output, IList<Sample[]> input, int n, WorkModes mode)
        {
            // Apply pending parameter changes: Inertia first, then targets (as the original).
            if (_inertiaDirty) { _inertiaDirty = false; core.SetInertia(_inertia); }
            if (_targetsDirty)
            {
                _targetsDirty = false;
                int spt = 0;
                try { spt = host.MasterInfo.SamplesPerTick; } catch { }
                if (spt <= 0) spt = 5512;
                core.SetTargets(_fade, _channels, _curve == 1, spt);
            }

            Sample[] outBuf = (output != null && output.Count > 0) ? output[0] : null;
            EnsureScratch(n);

            int pins = input?.Count ?? 0;
            for (int c = 0; c < FaderCore.MaxChannels; c++)
            {
                Sample[] src = c < pins ? input[c] : null;
                if (src == null) { _inL[c] = null; _inR[c] = null; continue; }
                float[] l = _bufL[c], r = _bufR[c];
                for (int s = 0; s < n; s++) { l[s] = src[s].L; r[s] = src[s].R; }
                _inL[c] = l; _inR[c] = r;
            }

            // Run even with no output connected, so gains keep tracking the ramp.
            bool active = core.Process(_inL, _inR, _outL, _outR, n);

            if (outBuf == null) return false;
            for (int s = 0; s < n; s++) { outBuf[s].L = _outL[s]; outBuf[s].R = _outR[s]; }
            return active;
        }

        // ── Display hooks (resolved by name, Build §12) ───────────────────────────

        public string DescribeValue(IParameter parameter, int value)
        {
            switch (parameter?.Name)
            {
                case "Fade":     return FaderCore.DescribeFade(value, _channels, _curve == 1);
                case "Inertia":  return FaderCore.DescribeInertia(value);
                case "Channels": return value + " (A-" + (char)('A' + Math.Clamp(value, 2, 6) - 1) + ")";
                default:         return null;
            }
        }

        public string GetChannelName(bool input, int index)
        {
            if (!input) return index == 0 ? "Out" : "";
            if (index < 0 || index >= FaderCore.MaxChannels) return "";
            string name = ((char)('A' + index)).ToString();
            return index < _channels ? name : name + " (unused)";
        }

        // ── Right-click menu ──────────────────────────────────────────────────────

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => MessageBox.Show(
                        "Pedal C-fader v" + Version + "\n\n" +
                        "2 to 6 channel crossfader. Connect sources to pins A-F,\n" +
                        "set Channels, then sweep Fade. Inertia glides between\n" +
                        "positions over a number of ticks.\n\n" +
                        "Managed replica of kibibu Slice n Dice (Cameron Foale).",
                        "About Pedal C-fader")
                }
            }
        };
    }
}
