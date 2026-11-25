using System;
using System.Globalization;
using System.IO.Ports;
using System.Threading;

namespace Sidralune.Devices.Serial
{
    /// <summary>
    /// A Starlight Xpress AO unit on a serial port (address "COM3"): 9600 baud, 8N1. Commands are single letters, a step
    /// is 'G', a direction letter (N, S, T for east, W) and a five-digit count; the unit answers with the command's letter,
    /// or 'L' when a limit stopped the move. 'K' centres it ('R' unjams it when centring fails), 'V' reports the firmware.
    /// </summary>
    public class SxAo : DeviceBase, IAdaptiveOptics
    {
        private SerialPort _port;
        private readonly object _io = new object();
        private int _x, _y;
        public int MaxSteps { get; set; } = 45;
        public int PositionX => _x;
        public int PositionY => _y;
        public string Firmware { get; private set; }

        public SxAo(DeviceInfo info) { Info = info; }

        public override void Connect()
        {
            string port = Info.Address.Split(':')[0];
            _port = new SerialPort(port, 9600, Parity.None, 8, StopBits.One) { ReadTimeout = 1000, WriteTimeout = 1000, Handshake = Handshake.None };
            _port.Open();
            try
            {
                Firmware = Version();
                if (Firmware == "000") throw new InvalidOperationException(Core.Util.Lang.T("The SX AO reports firmware 000: it needs its firmware flashed again."));
                Center();
            }
            catch { try { _port.Close(); } catch { } _port = null; throw; }
            Connected = true;
        }

        public override void Disconnect() { try { _port?.Close(); } catch { } _port = null; Connected = false; }

        private char Send(string command, int timeoutMs = 1000)
        {
            lock (_io)
            {
                if (_port == null) throw new InvalidOperationException("The AO is not connected");
                _port.DiscardInBuffer();
                _port.ReadTimeout = timeoutMs;
                _port.Write(command);
                int b = _port.ReadByte();
                // a 'W' before the answer is passed over
                if (b == 'W' && command.Length == 1 && command[0] != 'W') b = _port.ReadByte();
                return (char)b;
            }
        }

        private string Version()
        {
            lock (_io)
            {
                if (Send("V") != 'V') throw new InvalidOperationException("The SX AO did not answer");
                Thread.Sleep(200);
                var d = new char[3];
                for (int i = 0; i < 3; i++) d[i] = (char)_port.ReadByte();
                return new string(d);
            }
        }

        public bool Step(AoDirection dir, int count)
        {
            if (count <= 0) return true;
            char letter = dir == AoDirection.North ? 'N' : dir == AoDirection.South ? 'S' : dir == AoDirection.East ? 'T' : 'W';
            char r = Send("G" + letter + Math.Min(99999, count).ToString("00000", CultureInfo.InvariantCulture));
            if (r != 'G' && r != 'L') throw new InvalidOperationException("The SX AO answered " + r + " to a step");
            // the unit does not report its place: it is counted, up to the travel
            int sign = dir == AoDirection.North || dir == AoDirection.West ? 1 : -1;
            if (dir == AoDirection.North || dir == AoDirection.South) _y = Clamp(_y + sign * count); else _x = Clamp(_x + sign * count);
            return r == 'G';
        }

        private int Clamp(int v) => Math.Max(-MaxSteps, Math.Min(MaxSteps, v));

        public void Center()
        {
            // centring drives to the stops and back: it may take a while
            if (Send("K", 45000) != 'K' && Send("R", 45000) != 'K') throw new InvalidOperationException("The SX AO could not centre");
            _x = _y = 0;
        }
    }
}
