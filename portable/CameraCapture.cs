using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace CameraBridge
{
    public interface ICameraSource : IDisposable
    {
        void Start();
        void CancelStart();
        void Stop();
        CameraFrame GetLatestFrame();
        string CameraName { get; }
        string LastError { get; }
        long FrameCount { get; }
    }

    // The graph owns all COM objects on one thread. Its callback only copies pixels;
    // a separate worker encodes the newest sample, never a queue of old pictures.
    public sealed class DirectShowCameraCapture : ICameraSource
    {
        private readonly string requestedName;
        private readonly int fps;
        private readonly int jpegQuality;
        private readonly bool mirror;
        private readonly object lifecycle = new object();
        private readonly object pixelsGate = new object();
        private readonly ManualResetEvent stopping = new ManualResetEvent(false);
        private readonly ManualResetEvent firstFrame = new ManualResetEvent(false);
        private readonly AutoResetEvent pixelsReady = new AutoResetEvent(false);
        private Thread graphThread;
        private Thread encoderThread;
        private bool disposed;
        private volatile bool startCancelled;
        private volatile CameraFrame latest;
        private volatile string cameraName;
        private volatile string lastError;
        private volatile PixelLayout layout;
        private RawFrame pendingPixels;
        private long frameCount;
        private long lastSampleTick;
        private long nextSampleTick;

        public DirectShowCameraCapture(string cameraName, int fps, int jpegQuality, bool mirror)
        {
            if (fps < 1 || fps > 30) throw new ArgumentOutOfRangeException("fps", "Camera rate must be 1 through 30.");
            if (jpegQuality < 1 || jpegQuality > 100) throw new ArgumentOutOfRangeException("jpegQuality", "JPEG quality must be 1 through 100.");
            requestedName = cameraName == null ? "auto" : cameraName.Trim();
            this.cameraName = requestedName;
            this.fps = fps;
            this.jpegQuality = jpegQuality;
            this.mirror = mirror;
        }

        public string CameraName { get { return cameraName; } }
        public string LastError { get { return lastError; } }
        public long FrameCount { get { return Interlocked.Read(ref frameCount); } }
        public CameraFrame GetLatestFrame() { return latest; }

        public static string[] ListDevices()
        {
            string[] result = null;
            Exception failure = null;
            Thread thread = new Thread(delegate()
            {
                try { result = EnumerateDevices(null, null); }
                catch (Exception ex) { failure = ex; }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            if (!thread.Join(10000)) throw new TimeoutException("Camera enumeration did not finish within 10 seconds.");
            if (failure != null) throw new InvalidOperationException("Could not enumerate cameras: " + failure.Message, failure);
            return result;
        }

        public void Start()
        {
            lock (lifecycle)
            {
                if (disposed) throw new ObjectDisposedException("DirectShowCameraCapture");
                if (graphThread != null && graphThread.IsAlive) throw new InvalidOperationException("Camera is already starting or running.");
                if (encoderThread != null && encoderThread.IsAlive) throw new InvalidOperationException("Camera is still stopping.");
                stopping.Reset();
                if (startCancelled)
                {
                    stopping.Set();
                    throw new InvalidOperationException("Camera start was cancelled.");
                }
                firstFrame.Reset();
                latest = null;
                lastError = null;
                Interlocked.Exchange(ref frameCount, 0);
                lock (pixelsGate) { pendingPixels = null; nextSampleTick = 0; }
                encoderThread = new Thread(EncodeLoop);
                encoderThread.IsBackground = true;
                encoderThread.Name = "Camera JPEG encoder";
                graphThread = new Thread(GraphLoop);
                graphThread.IsBackground = true;
                graphThread.Name = "Camera capture graph";
                graphThread.SetApartmentState(ApartmentState.MTA);
                if (startCancelled)
                {
                    stopping.Set();
                    throw new InvalidOperationException("Camera start was cancelled.");
                }
                encoderThread.Start();
                graphThread.Start();
            }
            int result = WaitHandle.WaitAny(new WaitHandle[] { firstFrame, stopping }, 15000);
            if (result == 0 && !stopping.WaitOne(0) && latest != null) return;
            string error = lastError;
            Stop();
            throw new InvalidOperationException(result == 1 ? "Camera start was cancelled." :
                "No camera image arrived within 15 seconds." + (String.IsNullOrEmpty(error) ? "" : " " + error));
        }

        // Permanently cancel this instance's pending startup without waiting for a
        // device driver. A normal Stop still permits a later Start on the instance.
        public void CancelStart()
        {
            startCancelled = true;
            stopping.Set();
            pixelsReady.Set();
        }

        public void Stop()
        {
            Thread graph;
            Thread encoder;
            lock (lifecycle)
            {
                stopping.Set();
                pixelsReady.Set();
                graph = graphThread;
                encoder = encoderThread;
            }
            // Do not hold application locks while Stop waits for DirectShow callbacks.
            if (graph != null && graph.IsAlive && graph != Thread.CurrentThread && !graph.Join(5000))
                lastError = "The camera driver did not finish stopping within 5 seconds.";
            if (encoder != null && encoder.IsAlive && encoder != Thread.CurrentThread && !encoder.Join(2000))
                lastError = "The camera encoder did not finish stopping within 2 seconds.";
            latest = null;
            lock (pixelsGate) { pendingPixels = null; }
        }

        public void Dispose()
        {
            lock (lifecycle) { if (disposed) return; disposed = true; }
            Stop();
            // Keep wait handles alive if a broken device driver still owns a callback.
            // SafeHandle finalizers release them once this capture object is collectable.
        }

        private void GraphLoop()
        {
            while (!stopping.WaitOne(0))
            {
                object graphObject = null, builderObject = null, sourceObject = null;
                object grabberObject = null, rendererObject = null;
                Native.ISampleGrabber grabber = null;
                Native.IMediaControl control = null;
                SampleCallback callback = null;
                try
                {
                    EnumerateDevices(requestedName, delegate(string name, IMoniker moniker)
                    {
                        Guid iid = typeof(Native.IBaseFilter).GUID;
                        moniker.BindToObject(null, null, ref iid, out sourceObject);
                        cameraName = name;
                    });
                    if (sourceObject == null) throw new InvalidOperationException("No matching camera was found: " + requestedName);
                    graphObject = Native.Create("E436EBB3-524F-11CE-9F53-0020AF0BA770");
                    builderObject = Native.Create("BF87B6E1-8C27-11D0-B3F0-00AA003761C5");
                    grabberObject = Native.Create("C1F400A0-3F08-11D3-9F0B-006008039E37");
                    rendererObject = Native.Create("C1F400A4-3F08-11D3-9F0B-006008039E37");
                    Native.IGraphBuilder graph = (Native.IGraphBuilder)graphObject;
                    Native.ICaptureGraphBuilder2 builder = (Native.ICaptureGraphBuilder2)builderObject;
                    Native.Check(builder.SetFiltergraph(graph), "Attach capture graph");
                    Native.Check(graph.AddFilter((Native.IBaseFilter)sourceObject, "Camera"), "Add camera");
                    ConfigureFormat(builder, (Native.IBaseFilter)sourceObject);
                    Native.Check(graph.AddFilter((Native.IBaseFilter)grabberObject, "Pixels"), "Add pixel capture");
                    Native.Check(graph.AddFilter((Native.IBaseFilter)rendererObject, "Discard output"), "Add output sink");
                    grabber = (Native.ISampleGrabber)grabberObject;
                    Native.AMMediaType requested = new Native.AMMediaType();
                    requested.MajorType = Native.Video;
                    requested.SubType = Native.Rgb24;
                    Native.Check(grabber.SetMediaType(requested), "Request RGB camera pixels");
                    Native.Check(grabber.SetOneShot(false), "Enable continuous camera capture");
                    Native.Check(grabber.SetBufferSamples(false), "Disable redundant camera buffering");
                    Guid category = Native.Capture;
                    Guid video = Native.Video;
                    Native.Check(builder.RenderStream(ref category, ref video, sourceObject,
                        (Native.IBaseFilter)grabberObject, (Native.IBaseFilter)rendererObject), "Connect camera graph");
                    Native.AMMediaType connected = new Native.AMMediaType();
                    try
                    {
                        Native.Check(grabber.GetConnectedMediaType(connected), "Read camera pixel format");
                        layout = PixelLayout.Read(connected);
                    }
                    finally { Native.FreeMediaType(connected); }
                    callback = new SampleCallback(this);
                    Native.Check(grabber.SetCallback(callback, 1), "Attach camera callback");
                    Interlocked.Exchange(ref lastSampleTick, Stopwatch.GetTimestamp());
                    lock (pixelsGate) { nextSampleTick = 0; }
                    control = (Native.IMediaControl)graphObject;
                    // The camera already paces a live stream. A renderer clock can
                    // unnecessarily delay samples in this headless capture graph.
                    Native.Check(((Native.IMediaFilter)graphObject).SetSyncSource(IntPtr.Zero), "Disable display scheduling");
                    Native.Check(control.Run(), "Start camera");
                    while (!stopping.WaitOne(250))
                    {
                        if ((Stopwatch.GetTimestamp() - Interlocked.Read(ref lastSampleTick)) / (double)Stopwatch.Frequency > 5)
                            throw new IOException("Camera stopped delivering images; reconnecting.");
                    }
                }
                catch (Exception ex) { if (!stopping.WaitOne(0)) lastError = ex.Message; }
                finally
                {
                    if (control != null) { try { control.Stop(); } catch (COMException) { } }
                    if (grabber != null) { try { grabber.SetCallback(null, 1); } catch (COMException) { } }
                    layout = null;
                    lock (pixelsGate) { pendingPixels = null; }
                    Native.Release(rendererObject);
                    Native.Release(grabberObject);
                    Native.Release(sourceObject);
                    Native.Release(builderObject);
                    Native.Release(graphObject);
                    GC.KeepAlive(callback);
                }
                if (stopping.WaitOne(1000)) break;
            }
        }

        private void ReceivePixels(IntPtr pointer, int length)
        {
            if (pointer == IntPtr.Zero || stopping.WaitOne(0)) return;
            PixelLayout currentLayout = layout;
            if (currentLayout == null || length < currentLayout.ByteCount) return;
            long now = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref lastSampleTick, now);
            lock (pixelsGate)
            {
                // A small tolerance avoids dropping every other frame when the
                // hardware clock is fractionally slower than the requested rate.
                long interval = Stopwatch.Frequency / fps;
                if (now + interval / 10 < nextSampleTick) return;
                nextSampleTick = Math.Max(nextSampleTick + interval, now + interval / 2);
                byte[] bytes = new byte[currentLayout.ByteCount];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                pendingPixels = new RawFrame { Pixels = bytes, Layout = currentLayout, CapturedAtUtc = DateTime.UtcNow };
            }
            pixelsReady.Set();
        }

        private void EncodeLoop()
        {
            ImageCodecInfo codec = null;
            try
            {
                foreach (ImageCodecInfo candidate in ImageCodecInfo.GetImageEncoders())
                    if (candidate.FormatID == ImageFormat.Jpeg.Guid) { codec = candidate; break; }
            }
            catch (Exception ex) { lastError = "Could not initialize the system JPEG encoder: " + ex.Message; return; }
            if (codec == null) { lastError = "The system JPEG encoder is unavailable."; return; }
            while (!stopping.WaitOne(0))
            {
                WaitHandle.WaitAny(new WaitHandle[] { stopping, pixelsReady });
                if (stopping.WaitOne(0)) break;
                RawFrame raw;
                lock (pixelsGate) { raw = pendingPixels; pendingPixels = null; }
                if (raw == null) continue;
                try
                {
                    byte[] jpeg = EncodeJpeg(raw, codec, jpegQuality, mirror);
                    if (stopping.WaitOne(0)) break;
                    latest = new CameraFrame { Jpeg = jpeg, CapturedAtUtc = raw.CapturedAtUtc };
                    Interlocked.Increment(ref frameCount);
                    lastError = null;
                    firstFrame.Set();
                }
                catch (Exception ex) { if (!stopping.WaitOne(0)) lastError = "Could not encode camera image: " + ex.Message; }
            }
        }

        private static byte[] EncodeJpeg(RawFrame raw, ImageCodecInfo codec, int quality, bool mirror)
        {
            PixelLayout format = raw.Layout;
            using (Bitmap source = new Bitmap(format.Width, format.Height, PixelFormat.Format24bppRgb))
            {
                BitmapData pixels = source.LockBits(new Rectangle(0, 0, source.Width, source.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    for (int y = 0; y < format.Height; y++)
                    {
                        int inputRow = format.BottomUp ? format.Height - 1 - y : y;
                        Marshal.Copy(raw.Pixels, inputRow * format.Stride,
                            IntPtr.Add(pixels.Scan0, y * pixels.Stride), format.Width * 3);
                    }
                }
                finally { source.UnlockBits(pixels); }
                using (Bitmap output = new Bitmap(320, 240, PixelFormat.Format24bppRgb))
                {
                    using (Graphics graphics = Graphics.FromImage(output))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.InterpolationMode = InterpolationMode.Bilinear;
                        graphics.PixelOffsetMode = PixelOffsetMode.Half;
                        graphics.DrawImage(source, new Rectangle(0, 0, 320, 240), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
                    }
                    if (mirror) output.RotateFlip(RotateFlipType.RotateNoneFlipX);
                    using (EncoderParameters parameters = new EncoderParameters(1))
                    using (MemoryStream stream = new MemoryStream())
                    {
                        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                        output.Save(stream, codec, parameters);
                        byte[] bytes = stream.ToArray();
                        if (bytes.Length < 4 || bytes.Length > 500 * 1024 || bytes[0] != 0xff || bytes[1] != 0xd8 ||
                            bytes[bytes.Length - 2] != 0xff || bytes[bytes.Length - 1] != 0xd9)
                            throw new IOException("The camera encoder returned an invalid or oversized JPEG.");
                        return bytes;
                    }
                }
            }
        }

        private void ConfigureFormat(Native.ICaptureGraphBuilder2 builder, Native.IBaseFilter source)
        {
            Guid category = Native.Capture, video = Native.Video, iid = typeof(Native.IAMStreamConfig).GUID;
            object configObject = null;
            List<FormatCandidate> candidates = new List<FormatCandidate>();
            IntPtr caps = IntPtr.Zero;
            try
            {
                if (builder.FindInterface(ref category, ref video, source, ref iid, out configObject) < 0 || configObject == null) return;
                Native.IAMStreamConfig config = (Native.IAMStreamConfig)configObject;
                int count, size;
                if (config.GetNumberOfCapabilities(out count, out size) < 0 || count < 1 || count > 1024 || size < 1 || size > 65536) return;
                caps = Marshal.AllocCoTaskMem(size);
                for (int index = 0; index < count; index++)
                {
                    IntPtr pointer = IntPtr.Zero;
                    Native.AMMediaType type = null;
                    try
                    {
                        if (config.GetStreamCaps(index, out pointer, caps) < 0 || pointer == IntPtr.Zero) continue;
                        type = (Native.AMMediaType)Marshal.PtrToStructure(pointer, typeof(Native.AMMediaType));
                        Native.BitmapInfoHeader bitmap = Native.ReadBitmapHeader(type);
                        if (bitmap.Height < -2160 || bitmap.Height > 2160) continue;
                        int height = Math.Abs(bitmap.Height);
                        if (bitmap.Width < 1 || bitmap.Width > 4096 || height < 1 || height > 2160) continue;
                        long interval = 10000000L / fps;
                        if (size >= Marshal.SizeOf(typeof(Native.VideoStreamConfigCaps)))
                        {
                            Native.VideoStreamConfigCaps capability = (Native.VideoStreamConfigCaps)Marshal.PtrToStructure(caps, typeof(Native.VideoStreamConfigCaps));
                            if (capability.MinFrameInterval > 0 && capability.MaxFrameInterval >= capability.MinFrameInterval)
                                interval = Math.Min(capability.MaxFrameInterval, Math.Max(capability.MinFrameInterval, interval));
                        }
                        long score = (bitmap.Width == 320 && height == 240 ? 0 : 100000000L) +
                            Math.Abs((long)bitmap.Width * height - 320 * 240) + Math.Abs(interval - 10000000L / fps);
                        candidates.Add(new FormatCandidate { Pointer = pointer, Type = type, Interval = interval, Score = score });
                        pointer = IntPtr.Zero;
                        type = null;
                    }
                    catch (ArgumentException) { }
                    finally { Native.FreeMediaType(type); if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
                }
                candidates.Sort(delegate(FormatCandidate left, FormatCandidate right) { return left.Score.CompareTo(right.Score); });
                foreach (FormatCandidate candidate in candidates)
                {
                    // AvgTimePerFrame has the same offset in VIDEOINFOHEADER and VIDEOINFOHEADER2.
                    long originalInterval = Marshal.ReadInt64(candidate.Type.FormatPtr, 40);
                    Marshal.WriteInt64(candidate.Type.FormatPtr, 40, candidate.Interval);
                    if (config.SetFormat(candidate.Type) >= 0) return;
                    Marshal.WriteInt64(candidate.Type.FormatPtr, 40, originalInterval);
                    if (config.SetFormat(candidate.Type) >= 0) return;
                }
                // Drivers without a settable format can still negotiate RGB24;
                // the encoder resizes the negotiated dimensions to 320 by 240.
            }
            finally
            {
                foreach (FormatCandidate candidate in candidates)
                { Native.FreeMediaType(candidate.Type); Marshal.FreeCoTaskMem(candidate.Pointer); }
                if (caps != IntPtr.Zero) Marshal.FreeCoTaskMem(caps);
                Native.Release(configObject);
            }
        }

        private static string[] EnumerateDevices(string wanted, Action<string, IMoniker> selected)
        {
            List<string> names = new List<string>();
            object deviceEnumObject = null;
            IEnumMoniker enumerator = null;
            bool matched = false;
            try
            {
                deviceEnumObject = Native.Create("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
                Guid category = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
                int result = ((Native.ICreateDevEnum)deviceEnumObject).CreateClassEnumerator(ref category, out enumerator, 0);
                Native.Check(result, "Enumerate cameras");
                if (enumerator == null) return names.ToArray();
                IMoniker[] monikers = new IMoniker[1];
                while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
                {
                    object bagObject = null;
                    try
                    {
                        Guid iid = typeof(Native.IPropertyBag).GUID;
                        monikers[0].BindToStorage(null, null, ref iid, out bagObject);
                        object friendly;
                        Native.Check(((Native.IPropertyBag)bagObject).Read("FriendlyName", out friendly, IntPtr.Zero), "Read camera name");
                        string name = Convert.ToString(friendly);
                        names.Add(name);
                        if (!matched && selected != null && (String.IsNullOrEmpty(wanted) || String.Equals(wanted, "auto", StringComparison.OrdinalIgnoreCase) ||
                            String.Equals(wanted, name, StringComparison.OrdinalIgnoreCase)))
                        { selected(name, monikers[0]); matched = true; }
                    }
                    finally { Native.Release(bagObject); Native.Release(monikers[0]); monikers[0] = null; }
                }
                return names.ToArray();
            }
            finally { Native.Release(enumerator); Native.Release(deviceEnumObject); }
        }

        private sealed class RawFrame
        { public byte[] Pixels; public PixelLayout Layout; public DateTime CapturedAtUtc; }
        private sealed class FormatCandidate
        { public IntPtr Pointer; public Native.AMMediaType Type; public long Interval; public long Score; }
        private sealed class PixelLayout
        {
            public int Width, Height, Stride, ByteCount;
            public bool BottomUp;
            public static PixelLayout Read(Native.AMMediaType type)
            {
                Native.BitmapInfoHeader bitmap = Native.ReadBitmapHeader(type);
                if (type.MajorType != Native.Video || type.SubType != Native.Rgb24 || bitmap.BitCount != 24 || bitmap.Compression != 0 ||
                    bitmap.Width < 1 || bitmap.Width > 4096 || bitmap.Height == 0 || bitmap.Height < -2160 || bitmap.Height > 2160)
                    throw new InvalidOperationException("Camera did not provide a supported RGB24 image.");
                int height = Math.Abs(bitmap.Height);
                int stride = (bitmap.Width * 3 + 3) & ~3;
                if (bitmap.SizeImage > 0 && bitmap.SizeImage % height == 0 && bitmap.SizeImage / height >= stride)
                    stride = bitmap.SizeImage / height;
                if (stride > 65536 || (long)stride * height > 32 * 1024 * 1024)
                    throw new InvalidOperationException("Camera image buffer exceeds the supported size.");
                return new PixelLayout { Width = bitmap.Width, Height = height, Stride = stride, ByteCount = stride * height, BottomUp = bitmap.Height > 0 };
            }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class SampleCallback : Native.ISampleGrabberCB
        {
            private readonly DirectShowCameraCapture owner;
            public SampleCallback(DirectShowCameraCapture owner) { this.owner = owner; }
            public int SampleCB(double time, IntPtr sample) { return 0; }
            public int BufferCB(double time, IntPtr buffer, int length)
            {
                try { owner.ReceivePixels(buffer, length); }
                catch (Exception ex) { owner.lastError = "Could not read camera pixels: " + ex.Message; }
                return 0;
            }
        }

        private static class Native
        {
            public static readonly Guid Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
            public static readonly Guid Rgb24 = new Guid("E436EB7D-524F-11CE-9F53-0020AF0BA770");
            public static readonly Guid Capture = new Guid("FB6C4281-0353-11D1-905F-0000C0CC16BA");
            private static readonly Guid VideoInfo = new Guid("05589F80-C356-11CE-BF01-00AA0055595A");
            private static readonly Guid VideoInfo2 = new Guid("F72A76A0-EB0A-11D0-ACE4-0000C0CC16BA");
            public static object Create(string clsid) { return Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(clsid), true)); }
            public static void Check(int result, string operation)
            { if (result < 0) throw new COMException(operation + " failed (0x" + result.ToString("X8") + ").", result); }
            public static void Release(object value)
            { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
            public static void FreeMediaType(AMMediaType type)
            {
                if (type == null) return;
                if (type.FormatPtr != IntPtr.Zero) { Marshal.FreeCoTaskMem(type.FormatPtr); type.FormatPtr = IntPtr.Zero; }
                if (type.Unknown != IntPtr.Zero) { Marshal.Release(type.Unknown); type.Unknown = IntPtr.Zero; }
            }
            public static BitmapInfoHeader ReadBitmapHeader(AMMediaType type)
            {
                int offset = type.FormatType == VideoInfo ? 48 : type.FormatType == VideoInfo2 ? 72 : -1;
                if (offset < 0 || type.FormatPtr == IntPtr.Zero || type.FormatSize < offset + 40)
                    throw new ArgumentException("Camera media type does not contain a video format.");
                return (BitmapInfoHeader)Marshal.PtrToStructure(IntPtr.Add(type.FormatPtr, offset), typeof(BitmapInfoHeader));
            }

            [StructLayout(LayoutKind.Sequential)]
            public sealed class AMMediaType
            {
                public Guid MajorType, SubType;
                [MarshalAs(UnmanagedType.Bool)] public bool FixedSizeSamples;
                [MarshalAs(UnmanagedType.Bool)] public bool TemporalCompression;
                public int SampleSize;
                public Guid FormatType;
                public IntPtr Unknown;
                public int FormatSize;
                public IntPtr FormatPtr;
            }
            [StructLayout(LayoutKind.Sequential)]
            public struct BitmapInfoHeader
            {
                public int Size, Width, Height;
                public short Planes, BitCount;
                public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
            }
            [StructLayout(LayoutKind.Sequential)]
            public struct VideoStreamConfigCaps
            {
                public Guid Guid;
                public int VideoStandard;
                public int InputX, InputY, MinCropX, MinCropY, MaxCropX, MaxCropY;
                public int CropGranularityX, CropGranularityY, CropAlignX, CropAlignY;
                public int MinOutputX, MinOutputY, MaxOutputX, MaxOutputY;
                public int OutputGranularityX, OutputGranularityY, StretchTapsX, StretchTapsY, ShrinkTapsX, ShrinkTapsY;
                public long MinFrameInterval, MaxFrameInterval;
                public int MinBitsPerSecond, MaxBitsPerSecond;
            }
            [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface ICreateDevEnum
            { [PreserveSig] int CreateClassEnumerator(ref Guid category, out IEnumMoniker enumerator, int flags); }
            [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IPropertyBag
            { [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr errorLog); }
            [ComImport, Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IBaseFilter { }
            [ComImport, Guid("56A868A9-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IGraphBuilder
            { [PreserveSig] int AddFilter(IBaseFilter filter, [MarshalAs(UnmanagedType.LPWStr)] string name); }
            [ComImport, Guid("56A868B1-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
            public interface IMediaControl
            { [PreserveSig] int Run(); [PreserveSig] int Pause(); [PreserveSig] int Stop(); }
            [ComImport, Guid("56A86899-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IMediaFilter
            {
                [PreserveSig] int GetClassID(out Guid clsid);
                [PreserveSig] int Stop();
                [PreserveSig] int Pause();
                [PreserveSig] int Run(long start);
                [PreserveSig] int GetState(int timeout, out int state);
                [PreserveSig] int SetSyncSource(IntPtr clock);
            }
            [ComImport, Guid("93E5A4E0-2D50-11D2-ABFA-00A0C9C6E38D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface ICaptureGraphBuilder2
            {
                [PreserveSig] int SetFiltergraph(IGraphBuilder graph);
                [PreserveSig] int GetFiltergraph(out IGraphBuilder graph);
                [PreserveSig] int SetOutputFileName(ref Guid type, [MarshalAs(UnmanagedType.LPWStr)] string fileName, out IBaseFilter filter, [MarshalAs(UnmanagedType.IUnknown)] out object sink);
                [PreserveSig] int FindInterface(ref Guid category, ref Guid type, IBaseFilter filter, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
                [PreserveSig] int RenderStream(ref Guid category, ref Guid type, [MarshalAs(UnmanagedType.IUnknown)] object source, IBaseFilter compressor, IBaseFilter renderer);
            }
            [ComImport, Guid("C6E13340-30AC-11D0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IAMStreamConfig
            {
                [PreserveSig] int SetFormat([In, MarshalAs(UnmanagedType.LPStruct)] AMMediaType type);
                [PreserveSig] int GetFormat(out IntPtr type);
                [PreserveSig] int GetNumberOfCapabilities(out int count, out int size);
                [PreserveSig] int GetStreamCaps(int index, out IntPtr type, IntPtr caps);
            }
            [ComImport, Guid("6B652FFF-11FE-4FCE-92AD-0266B5D7C78F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface ISampleGrabber
            {
                [PreserveSig] int SetOneShot([MarshalAs(UnmanagedType.Bool)] bool oneShot);
                [PreserveSig] int SetMediaType([In, MarshalAs(UnmanagedType.LPStruct)] AMMediaType type);
                [PreserveSig] int GetConnectedMediaType([Out, MarshalAs(UnmanagedType.LPStruct)] AMMediaType type);
                [PreserveSig] int SetBufferSamples([MarshalAs(UnmanagedType.Bool)] bool buffer);
                [PreserveSig] int GetCurrentBuffer(ref int size, IntPtr buffer);
                [PreserveSig] int GetCurrentSample(out IntPtr sample);
                [PreserveSig] int SetCallback(ISampleGrabberCB callback, int method);
            }
            [ComVisible(true), Guid("0579154A-2B53-4994-B0D0-E773148EFF85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface ISampleGrabberCB
            {
                [PreserveSig] int SampleCB(double time, IntPtr sample);
                [PreserveSig] int BufferCB(double time, IntPtr buffer, int length);
            }
        }
    }
}
