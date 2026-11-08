using System;

namespace MW2.Formats
{
    /// <summary>Hud_UpdatePlayer 0x100176cb and MIDI hum 0x1003fa00/1003fb0e/1003fbbe/1003fc16.
    /// The sink receives WinMM short messages: the original Miles path was captured
    /// forwarding these same bytes. Channel 8 is the original standalone hum allocation;
    /// shared allocation with XMID music is not implemented.</summary>
    public sealed class MidiPowerHum : IDisposable
    {
        readonly Action<uint> _send;
        readonly int _channel;
        bool _active;
        public MidiPowerHum(Action<uint> send, int channel = 8) { _send = send; _channel = channel; }
        void Send(int status, int a, int b) => _send((uint)((status | _channel) | (a << 8) | (b << 16)));

        /// <summary>0x100176cb: HUD-off and death/ejection skip this path; power-up starts,
        /// running adjusts, other states stop. The external camera mutes after the state handler.</summary>
        public void Update(int state, int pitchInput, int effectsVolume, bool hudVisible, bool inCockpit)
        {
            if (!hudVisible || state == 4 || state == 5) return;
            if (state == 1 && !_active)
            {
                Send(0xb0, 0x72, 0); Send(0xc0, 3, 0); Send(0xe0, 0, 0x30);
                Send(0xb0, 10, 0x40); Send(0xb0, 7, 0); Send(0x90, 0x26, 0x7f);
                _active = true;
            }
            else if (state == 2 && _active)
            {
                uint bend = unchecked((uint)(pitchInput * 4 + 0x2000));
                Send(0xe0, (int)(bend & 0x7f), (int)((bend & 0x3f00) >> 8));
                // FUN_100076a0: signed 16.16 product, rounded by its bit 15, then low byte.
                long product = (long)effectsVolume * 100;
                int volume = (int)(product >> 16) + ((product & 0x8000) != 0 ? 1 : 0);
                Send(0xb0, 7, volume & 0xff);
            }
            else if (state != 1 && state != 2) Stop();
            if (!inCockpit) Mute();
        }

        /// <summary>FUN_1003fc16, also called by Sound_PauseAll 0x1000fd4b.</summary>
        public void Mute() { if (_active) Send(0xb0, 7, 0); }
        /// <summary>FUN_1003fbbe: note off before releasing the channel.</summary>
        public void Stop() { if (_active) { Send(0x80, 0x26, 0x7f); _active = false; } }
        public void Dispose() => Stop();
    }
}
