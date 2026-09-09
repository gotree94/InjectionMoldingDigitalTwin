using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace InjectionMoldingDT.PLC
{
    /// <summary>
    /// 미쓰비시 PLC 디바이스 코드 (MC Protocol 3E 프레임, Q/iQ-R/iQ-F 공통)
    /// </summary>
    public enum PlcDevice : byte
    {
        X = 0x9C,   // 입력
        Y = 0x9D,   // 출력
        M = 0x90,   // 내부 릴레이
        L = 0x92,   // 래치 릴레이
        F = 0x93,   // 아나운시에이터
        V = 0x94,   // 엣지 릴레이
        B = 0xA0,   // 링크 릴레이
        D = 0xA8,   // 데이터 레지스터 (워드)
        W = 0xB4,   // 링크 레지스터
        TS = 0xC1,  // 타이머 접점
        TC = 0xC0,  // 타이머 코일
        TN = 0xC2,  // 타이머 현재값
        CS = 0xC4,  // 카운터 접점
        CC = 0xC3,  // 카운터 코일
        CN = 0xC5,  // 카운터 현재값
        S = 0x98,   // 스텝 릴레이
        SM = 0x91,  // 스페셜 릴레이
        SD = 0xA9,  // 스페셜 레지스터
        R = 0xAF,   // 파일 레지스터
        ZR = 0xB0,  // 파일 레지스터 (연속)
        Z = 0xCC,   // 인덱스 레지스터
    }

    /// <summary>
    /// MC Protocol 3E 프레임(바이너리) TCP 클라이언트.
    /// 프레임 구조는 McpX(https://github.com/YudaiKitamura/McpX) 검증 구현을 기준으로 함.
    ///
    /// 요청 구조:
    ///   [0:2]  SubHeader(0x50 0x00)
    ///   [2:1]  Network No(0x00)
    ///   [3:1]  PC No(0xFF)
    ///   [4:1]  Unit No(0xFF)
    ///   [5:1]  IO No(0x03)
    ///   [6:1]  Station No(0x00)
    ///   [7:2]  데이터 길이 (모니터 타이머 ~ payload)
    ///   [9:2]  Monitoring Timer (1 = 250ms, little-endian)
    ///   [11:2] Command        (읽기 0x0401 / 쓰기 0x1401, little-endian)
    ///   [13:2] Sub Command    (워드 0x0000 / 비트 0x0001, little-endian)
    ///   이후 payload:
    ///     읽기: [주소 3바이트 LE][디바이스 코드 1바이트][개수 2바이트 LE]
    ///     쓰기: [주소 3바이트 LE][디바이스 코드 1바이트][개수 2바이트 LE][데이터..]
    ///
    /// 응답 구조:
    ///   [0:2] SubHeader / [2:7] Route / [7:2] 데이터 길이 / [9:2] 에러 코드 / [11:] 데이터
    /// </summary>
    public class McProtocolClient : IDisposable
    {
        private readonly string _ip;
        private readonly int _port;
        private readonly int _timeoutMs;
        private readonly object _sync = new object();

        private TcpClient _client;
        private NetworkStream _stream;
        private bool _connected;

        public bool IsConnected
        {
            get { lock (_sync) { return _connected; } }
        }

        public string LastError { get; private set; }

        public McProtocolClient(string ip, int port = 5001, int timeoutMs = 2000)
        {
            _ip = ip;
            _port = port;
            _timeoutMs = timeoutMs;
            LastError = string.Empty;
        }

        public bool Connect()
        {
            lock (_sync)
            {
                try
                {
                    if (_connected) return true;

                    _client = new TcpClient();
                    _client.SendTimeout = _timeoutMs;
                    _client.ReceiveTimeout = _timeoutMs;

                    var task = _client.ConnectAsync(_ip, _port);
                    if (!task.Wait(_timeoutMs))
                    {
                        _client.Close();
                        LastError = "Connection timeout.";
                        return false;
                    }

                    _stream = _client.GetStream();
                    _connected = true;
                    LastError = string.Empty;
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    _connected = false;
                    return false;
                }
            }
        }

        public void Disconnect()
        {
            lock (_sync)
            {
                try
                {
                    if (_stream != null) _stream.Close();
                    if (_client != null) _client.Close();
                }
                catch { /* ignore */ }
                finally
                {
                    _stream = null;
                    _client = null;
                    _connected = false;
                }
            }
        }

        //--------------------------------------------------------------
        // 공개 고수준 API
        //--------------------------------------------------------------

        /// <summary>워드(16비트) 연속 읽기.</summary>
        public ushort[] ReadWords(PlcDevice device, int address, int count)
        {
            byte[] request = BuildBatchReadRequest(device, address, count, false);
            byte[] response = Request(request);
            return ParseWordValues(response, count);
        }

        /// <summary>비트 연속 읽기.</summary>
        public bool[] ReadBits(PlcDevice device, int address, int count)
        {
            byte[] request = BuildBatchReadRequest(device, address, count, true);
            byte[] response = Request(request);
            return ParseBitValues(response, count);
        }

        /// <summary>워드 연속 쓰기.</summary>
        public void WriteWords(PlcDevice device, int address, ushort[] values)
        {
            byte[] payload = new byte[values.Length * 2];
            for (int i = 0; i < values.Length; i++)
            {
                byte[] w = BitConverter.GetBytes(values[i]);
                payload[i * 2] = w[0];
                payload[i * 2 + 1] = w[1];
            }
            byte[] request = BuildBatchWriteRequest(device, address, values.Length, false, payload);
            Request(request);
        }

        /// <summary>개별 워드 1개 쓰기.</summary>
        public void WriteWord(PlcDevice device, int address, ushort value)
        {
            WriteWords(device, address, new[] { value });
        }

        /// <summary>비트 연속 쓰기.</summary>
        public void WriteBits(PlcDevice device, int address, bool[] values)
        {
            // 비트 데이터: 1바이트당 2비트 (첫 값 = high nibble 0x10, 둘째 = low bit 0x01)
            int byteCount = (values.Length + 1) / 2;
            byte[] payload = new byte[byteCount];
            for (int i = 0; i < values.Length; i++)
            {
                if (i % 2 == 0)
                {
                    payload[i / 2] = values[i] ? (byte)0x10 : (byte)0x00;
                }
                else
                {
                    if (values[i]) payload[i / 2] |= 0x01;
                }
            }
            byte[] request = BuildBatchWriteRequest(device, address, values.Length, true, payload);
            Request(request);
        }

        /// <summary>개별 비트 1개 쓰기.</summary>
        public void WriteBit(PlcDevice device, int address, bool value)
        {
            WriteBits(device, address, new[] { value });
        }

        //--------------------------------------------------------------
        // 프레임 생성
        //--------------------------------------------------------------

        private byte[] BuildBatchReadRequest(PlcDevice device, int address, int count, bool isBit)
        {
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);

            // 헤더
            bw.Write((byte)0x50); bw.Write((byte)0x00); // SubHeader
            bw.Write((byte)0x00);                       // Network No
            bw.Write((byte)0xFF);                       // PC No
            bw.Write((byte)0xFF);                       // Unit No
            bw.Write((byte)0x03);                       // IO No
            bw.Write((byte)0x00);                       // Station No
            bw.Write((ushort)0);                        // 데이터 길이(패딩) - 뒤에서 채움

            // 명령부
            bw.Write((ushort)(monitoringTimerIn250ms())); // Monitoring Timer (1 = 250ms)
            bw.Write((ushort)0x0401);                     // 명령: 배치 읽기
            bw.Write((ushort)(isBit ? 0x0001 : 0x0000));  // 서브명령: 비트/워드

            WriteDeviceAddress(bw, device, address);
            bw.Write((ushort)count);

            return Finalize(ms.ToArray(), bw);
        }

        private byte[] BuildBatchWriteRequest(PlcDevice device, int address, int count, bool isBit, byte[] data)
        {
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);

            bw.Write((byte)0x50); bw.Write((byte)0x00);
            bw.Write((byte)0x00);
            bw.Write((byte)0xFF);
            bw.Write((byte)0xFF);
            bw.Write((byte)0x03);
            bw.Write((byte)0x00);
            bw.Write((ushort)0);

            bw.Write((ushort)monitoringTimerIn250ms());
            bw.Write((ushort)0x1401);                     // 명령: 배치 쓰기
            bw.Write((ushort)(isBit ? 0x0001 : 0x0000));

            WriteDeviceAddress(bw, device, address);
            bw.Write((ushort)count);
            bw.Write(data);

            return Finalize(ms.ToArray(), bw);
        }

        private ushort monitoringTimerIn250ms()
        {
            // 모니터 타이머는 250ms 단위. 최소 1 (250ms). 0 또는 250ms 이상이어야 함.
            int v = _timeoutMs / 250;
            return (ushort)Math.Min(255, Math.Max(1, v));
        }

        private void WriteDeviceAddress(BinaryWriter bw, PlcDevice device, int address)
        {
            // X/Y 등 16진 디바이스도 address는 이미 16진수로 해석된 uint 값으로 전달된다고 가정.
            byte[] b = BitConverter.GetBytes((uint)address);
            // Q/L 계열: [주소 3바이트 LE][디바이스 코드 1바이트]
            bw.Write(b[0]);
            bw.Write(b[1]);
            bw.Write(b[2]);
            bw.Write((byte)device);
        }

        // 데이터 길이 필드(오프셋 7)를 뒤에서 계산해 채움
        private byte[] Finalize(byte[] packet, BinaryWriter bw)
        {
            int dataLength = packet.Length - 9; // 모니터 타이머 ~ payload 길이
            byte[] length = BitConverter.GetBytes((ushort)dataLength);
            packet[7] = length[0];
            packet[8] = length[1];
            return packet;
        }

        //--------------------------------------------------------------
        // 송수신 & 응답 파싱
        //--------------------------------------------------------------

        private byte[] Request(byte[] request)
        {
            lock (_sync)
            {
                if (!_connected || _stream == null)
                {
                    if (!Connect())
                    {
                        throw new IOException("PLC 연결 실패: " + LastError);
                    }
                }

                try
                {
                    _stream.Write(request, 0, request.Length);

                    // 헤더(9바이트) 읽기: sub(2)+route(5)+length(2)
                    byte[] header = ReadExactly(9);
                    ushort contentLength = BitConverter.ToUInt16(header, 7);

                    // 컨텐츠 읽기: 에러코드(2) + 데이터
                    byte[] content = ReadExactly(contentLength);
                    ushort errorCode = BitConverter.ToUInt16(content, 0);
                    if (errorCode != 0x0000)
                    {
                        throw new IOException(string.Format(
                            "PLC 응답 에러 코드 0x{0:X4}", errorCode));
                    }

                    byte[] data = new byte[content.Length - 2];
                    Array.Copy(content, 2, data, 0, data.Length);
                    return data;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    if (ex is SocketException || ex is IOException)
                    {
                        _connected = false;
                    }
                    throw;
                }
            }
        }

        private byte[] ReadExactly(int length)
        {
            var buffer = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = _stream.Read(buffer, offset, length - offset);
                if (read == 0)
                {
                    throw new IOException("PLC 연결이 종료되었습니다.");
                }
                offset += read;
            }
            return buffer;
        }

        private ushort[] ParseWordValues(byte[] data, int count)
        {
            var result = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = (ushort)(data[i * 2] | (data[i * 2 + 1] << 8));
            }
            return result;
        }

        private bool[] ParseBitValues(byte[] data, int count)
        {
            var result = new bool[count];
            for (int i = 0; i < count; i++)
            {
                byte b = data[i / 2];
                if (i % 2 == 0)
                    result[i] = (b & 0x10) != 0;
                else
                    result[i] = (b & 0x01) != 0;
            }
            return result;
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}