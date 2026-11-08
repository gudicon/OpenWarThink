using System;
using System.Runtime.InteropServices;

namespace MW2
{
    /// <summary>Windows host adapter for FUN_1000fdd6: MIDI Mapper first, device 0 on failure.
    /// The original WAIL32/Miles path uses this same WinMM device and forwards the
    /// hum messages unchanged. No generated waveform or substitute sample is used.</summary>
    public sealed class WindowsMidiOutput : IDisposable
    {
        [DllImport("winmm.dll")] static extern uint midiOutOpen(out IntPtr handle, uint device, UIntPtr callback, UIntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern uint midiOutShortMsg(IntPtr handle, uint message);
        [DllImport("winmm.dll")] static extern uint midiOutClose(IntPtr handle);
        IntPtr _handle;
        public static WindowsMidiOutput Open()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            var output = new WindowsMidiOutput();
            if (midiOutOpen(out output._handle, 0xffffffff, UIntPtr.Zero, UIntPtr.Zero, 0) != 0 &&
                midiOutOpen(out output._handle, 0, UIntPtr.Zero, UIntPtr.Zero, 0) != 0) return null;
            return output;
        }
        public void Send(uint message) { if (_handle != IntPtr.Zero) midiOutShortMsg(_handle, message); }
        public void Dispose() { if (_handle != IntPtr.Zero) { midiOutClose(_handle); _handle = IntPtr.Zero; } }
    }
}
