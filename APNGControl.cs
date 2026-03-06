using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Xml.Linq;

using GlobalStructures;
using static GlobalStructures.GlobalTools;
using Direct2D;
using DXGI;
using static DXGI.DXGITools;
using WIC;
using static WIC.WICTools;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

// https://www.w3.org/TR/png-3/#apng-frame-based-animation

namespace WinUI3_APNG
{
    public sealed class APNGControl : SwapChainPanel, IDisposable    
    {
        [ComImport, Guid("63aad0b8-7c24-40ff-85a8-640d944cc325"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ISwapChainPanelNative
        {
            [PreserveSig]
            HRESULT SetSwapChain(IDXGISwapChain swapChain);
        }

        private bool bDisposedValue; 

        private ApngImage? _apng;
        private ID2D1Bitmap1? _pD2DBitmapCanvas;
        private ID2D1Bitmap1? _pD2DBitmapPreviousCanvas;

        private int _nCurrentFrameIndex;
        private int _nNumPlaysDone;
        private double _nextFrameTimeMs;

        IntPtr m_hWndMain;

        ID2D1Factory1 m_pD2DFactory1 = null;
        IWICImagingFactory m_pWICImagingFactory = null;

        IntPtr m_pD3D11DevicePtr = IntPtr.Zero; // Released in CreateDeviceContext : not used
        ID3D11DeviceContext m_pD3D11DeviceContext = null; // Released in Clean : not used
        IDXGIDevice1 m_pDXGIDevice = null; // Released in Clean

        ID2D1DeviceContext m_pD2DDeviceContext = null; // Released in Clean

        IDXGISwapChain1 m_pDXGISwapChain1 = null;
        ID2D1Bitmap1 m_pD2DTargetBitmap = null;

        IWICBitmapDecoder m_pWICBitmapDecoder = null;
        ID2D1Bitmap m_pD2DBitmapFrame = null;       

        public APNGControl()
        {            
        }

        public void Init(IntPtr hWndMain, ID2D1Factory1 pD2DFactory1, IWICImagingFactory pWICImagingFactory)
        {
            m_hWndMain = hWndMain;
            m_pD2DFactory1 = pD2DFactory1;
            m_pWICImagingFactory = pWICImagingFactory;
            HRESULT hr = CreateDeviceContext();
            //hr = CreateDeviceResources();
            hr = CreateSwapChain(IntPtr.Zero);
            if (SUCCEEDED(hr))
            {
                hr = ConfigureSwapChain(m_hWndMain);
                ISwapChainPanelNative panelNative = WinRT.CastExtensions.As<ISwapChainPanelNative>(this);
                hr = panelNative.SetSwapChain(m_pDXGISwapChain1);
            }
            this.SizeChanged += APNGControl_SizeChanged;
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }

        private void CompositionTarget_Rendering(object? sender, object e)
        {
            Render();            
        }

        private void LoadApngFromFile(string path, ref int nWidth, ref int nHeight, ref int nNbFRames)
        {
            using var fs = System.IO.File.OpenRead(path);
            LoadApng(fs, ref nWidth, ref nHeight, ref nNbFRames);
        }

        // APNG parsing done with help from Copilot

        private void LoadApng(Stream stream, ref int nWidth, ref int nHeight, ref int nNbFRames)
        {
            DisposeResources();
            _apng = ParseApng(stream);
            nWidth = _apng.CanvasWidth;
            nHeight = _apng.CanvasHeight;
            nNbFRames = _apng.Frames.Count;
            _nCurrentFrameIndex = 0;
            _nNumPlaysDone = 0;
            _nextFrameTimeMs = 0;

            if (_apng == null || _apng.Frames.Count == 0)
                return;

            SafeRelease(ref _pD2DBitmapCanvas);
            D2D1_BITMAP_PROPERTIES1 bitmapProperties = new D2D1_BITMAP_PROPERTIES1();          
            bitmapProperties.pixelFormat = D2DTools.PixelFormat(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED);
            bitmapProperties.bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET;
            HRESULT hr = m_pD2DDeviceContext.CreateBitmap(new D2D1_SIZE_U((uint)_apng.CanvasWidth, (uint)_apng.CanvasHeight), IntPtr.Zero, 0, bitmapProperties, out _pD2DBitmapCanvas);

            // Frames decoding
            foreach (var frame in _apng.Frames)
            {
                using var ms = BuildFramePng(frame);
                frame.WICBitmap = DecodeWithWIC(ms, m_pWICImagingFactory);                
            }

            //m_renderTimer = DispatcherQueue.CreateTimer();
            //m_renderTimer.Interval = TimeSpan.FromMilliseconds(2);
            //m_renderTimer.IsRepeating = true;
            //m_renderTimer.Tick += (s, e) => Render();
            //m_renderTimer.Start();

            if (_stopWatch == null)
            {
                _stopWatch = new Stopwatch();
                _stopWatch.Start();
            }
            else
                _stopWatch.Restart();
        }

        //Microsoft.UI.Dispatching.DispatcherQueueTimer? m_renderTimer;

        private static IWICBitmap? DecodeWithWIC(MemoryStream pngStream, IWICImagingFactory pImagingFactory)
        {
            IWICBitmap pWICBitmap = null;
            IWICStream pWICStream = null;
            IWICBitmapDecoder pDecoder = null;
            IWICBitmapFrameDecode pFrameDecode = null;
            IWICFormatConverter pFormatConverter = null;
           
            HRESULT hr = pImagingFactory.CreateStream(out pWICStream);
            if (FAILED(hr)) goto Cleanup;

            // Read MemoryStream into byte[]
            byte[] data = pngStream.ToArray();
            hr = pWICStream.InitializeFromMemory(data, data.Length);
            if (FAILED(hr)) goto Cleanup;
           
            hr = pImagingFactory.CreateDecoderFromStream(
                pWICStream,
                GUID_VendorMicrosoft,
                WICDecodeOptions.WICDecodeMetadataCacheOnDemand,
                out pDecoder);
            if (FAILED(hr)) goto Cleanup;
            
            hr = pDecoder.GetFrame(0, out pFrameDecode);
            if (FAILED(hr)) goto Cleanup;
           
            hr = pImagingFactory.CreateFormatConverter(out pFormatConverter);
            if (FAILED(hr)) goto Cleanup;
           
            hr = pFormatConverter.Initialize(
                pFrameDecode,
                GUID_WICPixelFormat32bppPBGRA,
                WICBitmapDitherType.WICBitmapDitherTypeNone,
                null,
                0.0,
                WICBitmapPaletteType.WICBitmapPaletteTypeCustom);
            if (FAILED(hr)) goto Cleanup;

            // Copy pixels in RAM
            hr = pImagingFactory.CreateBitmapFromSource(pFormatConverter, WICBitmapCreateCacheOption.WICBitmapCacheOnLoad, out pWICBitmap);
            if (FAILED(hr)) goto Cleanup;

            Cleanup:
            SafeRelease(ref pFormatConverter);
            SafeRelease(ref pFrameDecode);
            SafeRelease(ref pDecoder);
            SafeRelease(ref pWICStream);

            return pWICBitmap;
        }

        private static IWICBitmap? DecodeWithWIC(Stream stream, IWICImagingFactory pImagingFactory)
        {
            IWICBitmap pWICBitmap = null;
            IWICStream pWICStream = null;
            IWICBitmapDecoder pDecoder = null;
            IWICBitmapFrameDecode pFrameDecode = null;
            IWICFormatConverter pFormatConverter = null;

            HRESULT hr = pImagingFactory.CreateStream(out pWICStream);
            if (FAILED(hr)) goto Cleanup;

            byte[] buffer = new byte[stream.Length];
            stream.Read(buffer, 0, buffer.Length);

            hr = pWICStream.InitializeFromMemory(buffer, buffer.Length);
            if (FAILED(hr)) goto Cleanup;

            hr = pImagingFactory.CreateDecoderFromStream(
                pWICStream,
                GUID_VendorMicrosoft,
                WICDecodeOptions.WICDecodeMetadataCacheOnDemand,
                out pDecoder);
            if (FAILED(hr)) goto Cleanup;

            hr = pDecoder.GetFrame(0, out pFrameDecode);
            if (FAILED(hr)) goto Cleanup;

            hr = pImagingFactory.CreateFormatConverter(out pFormatConverter);
            if (FAILED(hr)) goto Cleanup;

            hr = pFormatConverter.Initialize(
                pFrameDecode,
                GUID_WICPixelFormat32bppPBGRA,
                WICBitmapDitherType.WICBitmapDitherTypeNone,
                null,
                0.0,
                WICBitmapPaletteType.WICBitmapPaletteTypeCustom);
            if (FAILED(hr)) goto Cleanup;

            // Copy pixels in RAM
            hr = pImagingFactory.CreateBitmapFromSource(pFormatConverter, WICBitmapCreateCacheOption.WICBitmapCacheOnLoad, out pWICBitmap);
            if (FAILED(hr)) goto Cleanup;

            Cleanup:
            SafeRelease(ref pFormatConverter);
            SafeRelease(ref pFrameDecode);
            SafeRelease(ref pDecoder);
            SafeRelease(ref pWICStream);

            return pWICBitmap;
        }

        private static ID2D1Bitmap1 CreateD2DBitmap(IWICBitmapSource pWIC, ID2D1DeviceContext pD2DDeviceContext)
        {
            ID2D1Bitmap1 pD2DBitmap = null;
            D2D1_BITMAP_PROPERTIES1 bitmapProperties = new D2D1_BITMAP_PROPERTIES1();
            bitmapProperties.pixelFormat = D2DTools.PixelFormat(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED);
            bitmapProperties.dpiX = 96;
            bitmapProperties.dpiY = 96;
            HRESULT hr = pD2DDeviceContext.CreateBitmapFromWicBitmap(pWIC, bitmapProperties, out pD2DBitmap);
            return pD2DBitmap;
        }

        // From Copilot...

        // https://www.w3.org/TR/png-3/#3PNGsignature
        private static readonly byte[] PngSignature = new byte[]
        {
            137, 80, 78, 71, 13, 10, 26, 10
        };

        private static ApngImage ParseApng(Stream stream)
        {
            using var br = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

            var sig = br.ReadBytes(8);
            if (!sig.SequenceEqual(PngSignature))
                throw new InvalidDataException("Not a PNG file");

            byte[]? ihdrData = null;
            var globalChunks = new List<PngChunk>();
            var frames = new List<ApngFrame>();

            bool hasAcTL = false;
            int nNumFrames = 0;
            int nNumPlays = 0;

            ApngFrame? currentFrame = null;
            int nFrameIndex = 0;

            while (true)
            {
                uint nLength;
                try
                {
                    nLength = ReadUInt32BE(br);
                }
                catch (EndOfStreamException)
                {
                    break;
                }

                var typeBytes = br.ReadBytes(4);
                if (typeBytes.Length < 4)
                    throw new EndOfStreamException();

                string sType = System.Text.Encoding.ASCII.GetString(typeBytes);
                var data = br.ReadBytes((int)nLength);
                uint nCrc = ReadUInt32BE(br); // ignored

                switch (sType)
                {
                    case "IHDR":
                        ihdrData = data;
                        break;

                    case "acTL":
                        hasAcTL = true;
                        nNumFrames = (int)ReadUInt32BE(data, 0);
                        nNumPlays = (int)ReadUInt32BE(data, 4);
                        break;

                    case "fcTL":
                        currentFrame = ParseFcTL(data, ihdrData!, nFrameIndex++);
                        frames.Add(currentFrame);
                        break;

                    case "fdAT":
                        if (currentFrame == null)
                            throw new InvalidDataException("fdAT before fcTL");
                        currentFrame.DataChunks.Add(new PngChunk { Type = "fdAT", Data = data });
                        break;

                    case "IDAT":
                        if (!hasAcTL)
                        {
                            if (frames.Count == 0)
                            {
                                currentFrame = CreateDefaultFrameFromIHDR(ihdrData!, nFrameIndex++);
                                frames.Add(currentFrame);
                            }
                        }
                        if (currentFrame == null)
                            throw new InvalidDataException("IDAT without frame");
                        currentFrame.DataChunks.Add(new PngChunk { Type = "IDAT", Data = data });
                        break;

                    case "IEND":
                        goto Done;

                    default:
                        if (IsGlobalChunk(sType))
                        {
                            globalChunks.Add(new PngChunk { Type = sType, Data = data });
                        }
                        break;
                }
            }

        Done:
            if (ihdrData == null)
                throw new InvalidDataException("Missing IHDR");

            foreach (var f in frames)
            {
                if (f.IhdrChunkData == null || f.IhdrChunkData.Length == 0)
                    f.IhdrChunkData = ihdrData;
                f.GlobalChunks.AddRange(globalChunks);
            }

            int nCanvasWidth = (int)ReadUInt32BE(ihdrData, 0);
            int nCanvasHeight = (int)ReadUInt32BE(ihdrData, 4);

            return new ApngImage
            {
                CanvasWidth = nCanvasWidth,
                CanvasHeight = nCanvasHeight,
                NumPlays = hasAcTL ? nNumPlays : 0,
                Frames = frames
            };
        }

        private static bool IsGlobalChunk(string sType)
        {
            return sType != "IHDR" && sType != "IDAT" && sType != "IEND" &&
                   sType != "acTL" && sType != "fcTL" && sType != "fdAT";
        }

        private static uint ReadUInt32BE(BinaryReader br)
        {
            var b = br.ReadBytes(4);
            if (b.Length < 4) throw new EndOfStreamException();
            return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        }      

        private static void WriteUInt32BE(byte[] data, int nOffset, uint nValue)
        {
            data[nOffset] = (byte)(nValue >> 24);
            data[nOffset + 1] = (byte)(nValue >> 16);
            data[nOffset + 2] = (byte)(nValue >> 8);
            data[nOffset + 3] = (byte)nValue;
        }

        // https://wiki.mozilla.org/APNG_Specification#%60fcTL%60:_The_Frame_Control_Chunk
        private static ApngFrame ParseFcTL(byte[] data, byte[] ihdrData, int nFrameIndex)
        {
            int nOffset = 0;
            uint nSeq = ReadUInt32BE(data, nOffset); nOffset += 4;
            uint nWidth = ReadUInt32BE(data, nOffset); nOffset += 4;
            uint nHeight = ReadUInt32BE(data, nOffset); nOffset += 4;
            uint nX = ReadUInt32BE(data, nOffset); nOffset += 4;
            uint nY = ReadUInt32BE(data, nOffset); nOffset += 4;

            ushort delayNum = (ushort)((data[nOffset] << 8) | data[nOffset + 1]); nOffset += 2;
            ushort delayDen = (ushort)((data[nOffset] << 8) | data[nOffset + 1]); nOffset += 2;

            byte disposeOp = data[nOffset++];
            byte blendOp = data[nOffset++];

            var ihdrClone = (byte[])ihdrData.Clone();
            WriteUInt32BE(ihdrClone, 0, nWidth);
            WriteUInt32BE(ihdrClone, 4, nHeight);

            if (delayDen == 0) delayDen = 100;

            return new ApngFrame
            {
                Index = nFrameIndex,
                XOffset = (int)nX,
                YOffset = (int)nY,
                Width = (int)nWidth,
                Height = (int)nHeight,
                DelayNum = delayNum,
                DelayDen = delayDen,
                DisposeOp = disposeOp,
                BlendOp = blendOp,
                IhdrChunkData = ihdrClone
            };
        }

        private static ApngFrame CreateDefaultFrameFromIHDR(byte[] ihdrData, int nFrameIndex)
        {
            int nWidth = (int)ReadUInt32BE(ihdrData, 0);
            int nHeight = (int)ReadUInt32BE(ihdrData, 4);

            return new ApngFrame
            {
                Index = nFrameIndex,
                XOffset = 0,
                YOffset = 0,
                Width = nWidth,
                Height = nHeight,
                DelayNum = 1,
                DelayDen = 10,
                DisposeOp = 0,
                BlendOp = 0,
                IhdrChunkData = ihdrData
            };
        }
        
        // Reconstruct PNG per frame       

        private static MemoryStream BuildFramePng(ApngFrame frame)
        {
            var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);

            // Signature
            bw.Write(PngSignature);

            // IHDR
            WriteChunk(bw, "IHDR", frame.IhdrChunkData);

            // Global chunks
            foreach (var c in frame.GlobalChunks)
                WriteChunk(bw, c.Type, c.Data);

            // Data chunks
            foreach (var c in frame.DataChunks)
            {
                if (c.Type == "fdAT")
                {
                    var idatData = c.Data.AsSpan(4).ToArray();
                    WriteChunk(bw, "IDAT", idatData);
                }
                else
                {
                    WriteChunk(bw, c.Type, c.Data);
                }
            }

            // IEND
            WriteChunk(bw, "IEND", Array.Empty<byte>());

            ms.Position = 0;
            return ms;
        }

        private static void WriteChunk(BinaryWriter bw, string sType, byte[] data)
        {
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(sType);
            bw.Write(ToBE((uint)data.Length));
            bw.Write(typeBytes);
            bw.Write(data);

            uint nCrc = Crc32(typeBytes, data);
            bw.Write(ToBE(nCrc));
        }

        private static byte[] ToBE(uint nValue)
        {
            return new[]
            {
                (byte)(nValue >> 24),
                (byte)(nValue >> 16),
                (byte)(nValue >> 8),
                (byte)nValue
            };
        }

        // Simple CRC32
        private static readonly uint[] CrcTable = CreateCrcTable();

        private static uint Crc32(byte[] type, byte[] data)
        {
            uint nCrc = 0xffffffffu;
            foreach (var b in type)
                nCrc = CrcTable[(nCrc ^ b) & 0xff] ^ (nCrc >> 8);
            foreach (var b in data)
                nCrc = CrcTable[(nCrc ^ b) & 0xff] ^ (nCrc >> 8);
            return nCrc ^ 0xffffffffu;
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    if ((c & 1) != 0)
                        c = 0xedb88320u ^ (c >> 1);
                    else
                        c >>= 1;
                }
                table[n] = c;
            }
            return table;
        }

        public bool LoadFile(string sFile, TextBlock tbWidth = null, TextBlock tbHeight = null, TextBlock tbAnimation = null)
        {
            bool bFileOK = false;

            string ext = System.IO.Path.GetExtension(sFile).ToLowerInvariant();
            if (ext == ".png")
            {
                if (IsApngFile(sFile, out bool bPNGNormal))
                {
                    bFileOK = true;
                    int nWidth = 0, nHeight = 0, nNbFrames = 0;
                    LoadApngFromFile(sFile, ref nWidth, ref nHeight, ref nNbFrames);
                    if (tbWidth != null)
                        tbWidth.Text = nWidth.ToString();
                    if (tbHeight != null)
                        tbHeight.Text = nHeight.ToString();
                    if (tbAnimation != null)
                    {
                        tbAnimation.Text = "Yes" ;
                        tbAnimation.Text += (" (" + nNbFrames.ToString() + " frames)");
                    }
                    this.Width = nWidth;
                    this.Height = nHeight;
                }
                else if (bPNGNormal)
                {  
                    bFileOK = true;
                    int nWidth = 0, nHeight = 0;
                    LoadStaticPngFromFile(sFile, ref nWidth, ref nHeight); //  PNG normal
                    if (tbWidth != null)
                        tbWidth.Text = nWidth.ToString();
                    if (tbHeight != null)
                        tbHeight.Text = nHeight.ToString();
                    if (tbAnimation != null)
                    {
                        tbAnimation.Text = "No";
                    }
                    this.Width = nWidth;
                    this.Height = nHeight;
                }
            }
            return bFileOK;
        }

        private void LoadStaticPngFromFile(string sPath, ref int nWidth, ref int nHeight)
        {
            SafeRelease(ref _pD2DBitmapCanvas);
            SafeRelease(ref _pD2DBitmapPreviousCanvas);
            _apng = null;
            _previousFrame = null;
           
            using var fs = File.OpenRead(sPath);
            IWICBitmapSource pWic = DecodeWithWIC(fs, m_pWICImagingFactory);           
            _pD2DBitmapCanvas = CreateD2DBitmap(pWic, m_pD2DDeviceContext);           
            pWic.GetSize(out uint nWidthWIC, out uint nHeightWIC);
            nWidth = (int)nWidthWIC;
            nHeight = (int)nHeightWIC;
            this.Width = nWidthWIC;
            this.Height = nHeightWIC;
           
            _stopWatch?.Stop();
        }

        private bool IsApngFile(string sPath, out bool bPNGNormal)
        {
            bPNGNormal = false;
            using var fs = System.IO.File.OpenRead(sPath);

            byte[] sig = new byte[8];
            if (fs.Read(sig, 0, 8) != 8)
                return false;

            // Signature PNG
            if (!sig.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                bPNGNormal = false;
                return false;
            }
            else
                bPNGNormal = true;

            while (true)
            {
                byte[] lenBuf = new byte[4];
                if (fs.Read(lenBuf, 0, 4) != 4)
                    break;

                uint nLen = ReadUInt32BE(lenBuf, 0);

                byte[] typeBuf = new byte[4];
                if (fs.Read(typeBuf, 0, 4) != 4)
                    break;

                string sType = Encoding.ASCII.GetString(typeBuf);

                if (sType == "acTL")
                    return true;

                fs.Position += nLen + 4; // skip data + CRC
            }

            return false;
        }

        private static uint ReadUInt32BE(byte[] data, int nOffset)
        {
            return (uint)(data[nOffset] << 24 | data[nOffset + 1] << 16 | data[nOffset + 2] << 8 | data[nOffset + 3]);
        }

        Stopwatch _stopWatch = null;       

        public HRESULT Render()
        {
            HRESULT hr = HRESULT.S_OK;
            if (m_pD2DDeviceContext != null)
            {
                //m_pD2DDeviceContext.BeginDraw();
                m_pD2DDeviceContext.GetSize(out D2D1_SIZE_F size); 

                float nWidth = size.width;
                float nHeight = size.height;
                D2D1_RECT_F destRect = new D2D1_RECT_F(0.0f, 0.0f, nWidth, nHeight);

                if (_apng != null && _pD2DBitmapCanvas != null)
                {
                    double tMs = _stopWatch.Elapsed.TotalMilliseconds;

                    // Advance as many frames as necessary
                    while (tMs >= _nextFrameTimeMs)
                    {
                        var frame = _apng.Frames[_nCurrentFrameIndex];
                       
                        if (_previousFrame != null)
                            ApplyDispose(_previousFrame);

                        // APNG_DISPOSE_OP_PREVIOUS
                        if (frame.DisposeOp == 2)
                        {
                            SafeRelease(ref _pD2DBitmapPreviousCanvas);
                            _pD2DBitmapPreviousCanvas = CloneBitmap(_pD2DBitmapCanvas, m_pD2DDeviceContext);
                        }

                        // Blend current frame
                        RenderApngFrame(frame);

                        _previousFrame = frame;
                       
                        int delayMs = ComputeDelayMs(frame);
                        _nextFrameTimeMs += delayMs;

                        _nCurrentFrameIndex++;
                        if (_nCurrentFrameIndex >= _apng.Frames.Count)
                        {
                            _nCurrentFrameIndex = 0;
                            _nNumPlaysDone++;
                        }
                    }
                }

                m_pD2DDeviceContext.SetTarget(m_pD2DTargetBitmap);
                m_pD2DDeviceContext.BeginDraw();
                
                // Important
                m_pD2DDeviceContext.Clear(new ColorF(ColorF.Enum.White, 0.0f));

                if (_pD2DBitmapCanvas != null)
                {
                    // copy _pD2DBitmapCanvas to Swapchain 
                    D2D1_RECT_F sourceRect = new D2D1_RECT_F(0.0f, 0.0f, nWidth, nHeight);
                    m_pD2DDeviceContext.DrawBitmap(_pD2DBitmapCanvas, ref destRect, 1.0f, D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, ref sourceRect);
                }

                hr = m_pD2DDeviceContext.EndDraw(out _, out _);

                if ((uint)hr == D2DTools.D2DERR_RECREATE_TARGET)
                {
                    m_pD2DDeviceContext.SetTarget(null);
                    SafeRelease(ref m_pD2DDeviceContext);
                    hr = CreateDeviceContext();
                    //CleanDeviceResources();
                    //hr = CreateDeviceResources();

                    SafeRelease(ref _pD2DBitmapCanvas);
                    if (_apng != null)
                    {
                        D2D1_BITMAP_PROPERTIES1 props = new D2D1_BITMAP_PROPERTIES1();
                        props.pixelFormat = D2DTools.PixelFormat(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED);
                        props.bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET;
                        m_pD2DDeviceContext.CreateBitmap(new D2D1_SIZE_U((uint)_apng.CanvasWidth, (uint)_apng.CanvasHeight),
                            IntPtr.Zero,
                            0,
                            props,
                            out _pD2DBitmapCanvas);
                    }

                    hr = CreateSwapChain(IntPtr.Zero);
                    hr = ConfigureSwapChain(m_hWndMain);
                }
                hr = m_pDXGISwapChain1.Present(1, 0);
                //hr = m_pDXGISwapChain1.Present(0, DXGI_PRESENT_ALLOW_TEARING);
            }
            return (hr);
        }

        private ApngFrame? _previousFrame;

        private static int ComputeDelayMs(ApngFrame frame)
        {
            ushort den = frame.DelayDen == 0 ? (ushort)100 : frame.DelayDen;
            double ms = 1000.0 * frame.DelayNum / den;
            if (ms < 10) ms = 10;
            return (int)ms;
        }

        private void RenderApngFrame(ApngFrame frame)
        {
            if (_pD2DBitmapCanvas == null || m_pD2DDeviceContext == null || frame.WICBitmap == null)
                return;

            // blend_op = SOURCE : clear zone before drawing
            if (frame.BlendOp == 0)
                ClearFrameArea(frame);

            m_pD2DDeviceContext.SetTarget(_pD2DBitmapCanvas);
            m_pD2DDeviceContext.BeginDraw();

            var pD2DBitmap = CreateD2DBitmap(frame.WICBitmap, m_pD2DDeviceContext);
            if (pD2DBitmap != null)
            {
                D2D1_RECT_F sourceRect = new D2D1_RECT_F(0.0f, 0.0f, frame.Width, frame.Height);
                m_pD2DDeviceContext.DrawBitmap(
                    pD2DBitmap,
                    new D2D1_RECT_F(
                        frame.XOffset,
                        frame.YOffset,
                        frame.XOffset + frame.Width,
                        frame.YOffset + frame.Height),
                    1.0f,
                    D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
                    ref sourceRect);
            }
            m_pD2DDeviceContext.EndDraw(out _, out _);
            SafeRelease(ref pD2DBitmap);
        }      

        private void ApplyDispose(ApngFrame frame)
        {
            switch (frame.DisposeOp)
            {
                case 0: // APNG_DISPOSE_OP_NONE                        
                    break;

                case 1: // APNG_DISPOSE_OP_BACKGROUND
                    ClearFrameArea(frame);
                    break;

                case 2: // APNG_DISPOSE_OP_PREVIOUS
                    RestorePreviousCanvas();
                    break;
            }
        }

        private void ClearFrameArea(ApngFrame frame)
        {
            HRESULT hr = HRESULT.S_OK;
            if (_pD2DBitmapCanvas == null || m_pD2DDeviceContext == null)
                return;

            m_pD2DDeviceContext.SetTarget(_pD2DBitmapCanvas);
            m_pD2DDeviceContext.BeginDraw();
            m_pD2DDeviceContext.PushAxisAlignedClip(
                new D2D1_RECT_F(
                    frame.XOffset,
                    frame.YOffset,
                    frame.XOffset + frame.Width,
                    frame.YOffset + frame.Height),
                D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
            m_pD2DDeviceContext.Clear(new ColorF(ColorF.Enum.Black, 0.0f));
            m_pD2DDeviceContext.PopAxisAlignedClip();
            hr = m_pD2DDeviceContext.EndDraw(out _, out _);
        }

        private void RestorePreviousCanvas()
        {
            HRESULT hr = HRESULT.S_OK;
            if (_pD2DBitmapCanvas == null || _pD2DBitmapPreviousCanvas == null || m_pD2DDeviceContext == null)
                return;

            m_pD2DDeviceContext.SetTarget(_pD2DBitmapCanvas);
            m_pD2DDeviceContext.BeginDraw();
            m_pD2DDeviceContext.Clear(new ColorF(ColorF.Enum.Black, 0.0f));
            D2D1_RECT_F sourceRect = new D2D1_RECT_F(0.0f, 0.0f, _apng!.CanvasWidth, _apng.CanvasHeight);
            m_pD2DDeviceContext.DrawBitmap(
                _pD2DBitmapPreviousCanvas,
                new D2D1_RECT_F(
                    0, 0,
                    _apng!.CanvasWidth,
                    _apng.CanvasHeight),
                1.0f,
                D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_NEAREST_NEIGHBOR,
                sourceRect);
            hr = m_pD2DDeviceContext.EndDraw(out _, out _);
        }

        private static ID2D1Bitmap1 CloneBitmap(ID2D1Bitmap1 pD2DBitmapSource, ID2D1DeviceContext pD2DDeviceContext)
        {            
            pD2DBitmapSource.GetPixelSize(out var size);
           
            D2D1_BITMAP_PROPERTIES1 bitmapProperties = new D2D1_BITMAP_PROPERTIES1();
            bitmapProperties.pixelFormat = D2DTools.PixelFormat(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED);
            bitmapProperties.bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET;

            HRESULT hr = pD2DDeviceContext.CreateBitmap(size, IntPtr.Zero, 0, bitmapProperties, out var pD2DBitmapClone);
           
            pD2DDeviceContext.SetTarget(pD2DBitmapClone);
            pD2DDeviceContext.BeginDraw();
            pD2DDeviceContext.Clear(new ColorF(ColorF.Enum.Black, 0.0f));
            var rect = new D2D1_RECT_F(0, 0, size.width, size.height);
            pD2DDeviceContext.DrawBitmap(pD2DBitmapSource, rect, 1.0f, D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_NEAREST_NEIGHBOR, rect);

            pD2DDeviceContext.EndDraw(out _, out _);

            return pD2DBitmapClone;
        }

        private void APNGControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Resize(e.NewSize);
        }

        HRESULT Resize(Windows.Foundation.Size sz)
        {
            HRESULT hr = HRESULT.S_OK;

            if (m_pDXGISwapChain1 != null)
            {
                if (m_pD2DDeviceContext != null)
                    m_pD2DDeviceContext.SetTarget(null);

                if (m_pD2DTargetBitmap != null)
                    SafeRelease(ref m_pD2DTargetBitmap);

                // 0, 0 => HRESULT: 0x80070057 (E_INVALIDARG) if not CreateSwapChainForHwnd
                //hr = m_pDXGISwapChain1.ResizeBuffers(
                // 2,
                // 0,
                // 0,
                // DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                // 0
                // );
                if (sz.Width != 0 && sz.Height != 0)
                {
                    hr = m_pDXGISwapChain1.ResizeBuffers(
                      2,
                      (uint)sz.Width,
                      (uint)sz.Height,
                      DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                      0/*(uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING*/
                      );
                }
                ConfigureSwapChain(m_hWndMain);
            }
            return (hr);
        }

        public HRESULT CreateDeviceContext()
        {
            HRESULT hr = HRESULT.S_OK;
            uint creationFlags = (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT;

            // Needs "Enable native code Debugging"
#if DEBUG
            creationFlags |= (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_DEBUG;
#endif

            int[] aD3D_FEATURE_LEVEL = new int[] { (int)D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, (int)D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                (int)D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_1, (int)D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_0, (int)D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_9_3,
                (int)D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_9_2, (int)D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_9_1};

            D3D_FEATURE_LEVEL featureLevel;
            hr = D2DTools.D3D11CreateDevice(null,    // specify null to use the default adapter
                D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                IntPtr.Zero,
                creationFlags,              // optionally set debug and Direct2D compatibility flags               
                aD3D_FEATURE_LEVEL,   // list of feature levels this app can support                
                (uint)aD3D_FEATURE_LEVEL.Length, // number of possible feature levels
                D2DTools.D3D11_SDK_VERSION,
                out m_pD3D11DevicePtr,       // returns the Direct3D device created
                out featureLevel,            // returns feature level of device created                                   
                out m_pD3D11DeviceContext  // returns the device immediate context
            );
            if (SUCCEEDED(hr))
            {
                m_pDXGIDevice = Marshal.GetObjectForIUnknown(m_pD3D11DevicePtr) as IDXGIDevice1;
                if (m_pD2DFactory1 != null)
                {
                    ID2D1Device pD2DDevice = null; // Released in CreateDeviceContext
                    hr = m_pD2DFactory1.CreateDevice(m_pDXGIDevice, out pD2DDevice);
                    if (SUCCEEDED(hr))
                    {
                        hr = pD2DDevice.CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, out m_pD2DDeviceContext);
                        SafeRelease(ref pD2DDevice);
                    }
                }
                //Marshal.Release(m_pD3D11DevicePtr);
            }
            return hr;
        }

        HRESULT CreateSwapChain(IntPtr hWnd)
        {
            HRESULT hr = HRESULT.S_OK;
            DXGI_SWAP_CHAIN_DESC1 swapChainDesc = new DXGI_SWAP_CHAIN_DESC1();
            swapChainDesc.Width = 1;
            swapChainDesc.Height = 1;
            swapChainDesc.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; // this is the most common swapchain format
            swapChainDesc.Stereo = false;
            swapChainDesc.SampleDesc.Count = 1;                // don't use multi-sampling
            swapChainDesc.SampleDesc.Quality = 0;
            swapChainDesc.BufferUsage = D2DTools.DXGI_USAGE_RENDER_TARGET_OUTPUT;
            swapChainDesc.BufferCount = 2;                     // use double buffering to enable flip
            swapChainDesc.Scaling = (hWnd != IntPtr.Zero) ? DXGI_SCALING.DXGI_SCALING_NONE : DXGI_SCALING.DXGI_SCALING_STRETCH;
            swapChainDesc.SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; // all apps must use this SwapEffect
                                                                                         
            //swapChainDesc.Flags = DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;

            IDXGIAdapter pDXGIAdapter;
            hr = m_pDXGIDevice.GetAdapter(out pDXGIAdapter);
            if (SUCCEEDED(hr))
            {
                IntPtr pDXGIFactory2Ptr;
                hr = pDXGIAdapter.GetParent(typeof(IDXGIFactory2).GUID, out pDXGIFactory2Ptr);
                if (SUCCEEDED(hr))
                {
                    IDXGIFactory2 pDXGIFactory2 = Marshal.GetObjectForIUnknown(pDXGIFactory2Ptr) as IDXGIFactory2;
                    if (hWnd != IntPtr.Zero)
                        hr = pDXGIFactory2.CreateSwapChainForHwnd(m_pD3D11DevicePtr, hWnd, ref swapChainDesc, IntPtr.Zero, null, out m_pDXGISwapChain1);
                    else
                    {
                        swapChainDesc.AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED;
                        hr = pDXGIFactory2.CreateSwapChainForComposition(m_pD3D11DevicePtr, ref swapChainDesc, null, out m_pDXGISwapChain1);
                    }
                    hr = m_pDXGIDevice.SetMaximumFrameLatency(1);
                    SafeRelease(ref pDXGIFactory2);
                    Marshal.Release(pDXGIFactory2Ptr);
                }
                SafeRelease(ref pDXGIAdapter);
            }
            return hr;
        }

        HRESULT ConfigureSwapChain(IntPtr hWnd)
        {
            HRESULT hr = HRESULT.S_OK;

            //IntPtr pD3D11Texture2DPtr = IntPtr.Zero;
            //hr = m_pDXGISwapChain1.GetBuffer(0, typeof(ID3D11Texture2D).GUID, ref pD3D11Texture2DPtr);
            //m_pD3D11Texture2D = Marshal.GetObjectForIUnknown(pD3D11Texture2DPtr) as ID3D11Texture2D;

            D2D1_BITMAP_PROPERTIES1 bitmapProperties = new D2D1_BITMAP_PROPERTIES1();
            bitmapProperties.bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW;
            bitmapProperties.pixelFormat = D2DTools.PixelFormat(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_IGNORE);
            //float nDpiX, nDpiY = 0.0f;
            //m_pD2DContext.GetDpi(out nDpiX, out nDpiY);
            uint nDPI = GetDpiForWindow(hWnd);
            bitmapProperties.dpiX = nDPI;
            bitmapProperties.dpiY = nDPI;

            IntPtr pDXGISurfacePtr = IntPtr.Zero;
            hr = m_pDXGISwapChain1.GetBuffer(0, typeof(IDXGISurface).GUID, out pDXGISurfacePtr);
            if (SUCCEEDED(hr))
            {
                IDXGISurface pDXGISurface = Marshal.GetObjectForIUnknown(pDXGISurfacePtr) as IDXGISurface;
                hr = m_pD2DDeviceContext.CreateBitmapFromDxgiSurface(pDXGISurface, ref bitmapProperties, out m_pD2DTargetBitmap);
                if (SUCCEEDED(hr))
                {
                    m_pD2DDeviceContext.SetTarget(m_pD2DTargetBitmap);
                }
                SafeRelease(ref pDXGISurface);
                Marshal.Release(pDXGISurfacePtr);
            }
            return hr;
        }

        void Clean()
        {
            DisposeResources();

            SafeRelease(ref _pD2DBitmapCanvas);
            SafeRelease(ref _pD2DBitmapPreviousCanvas);

            SafeRelease(ref m_pD2DTargetBitmap);
            SafeRelease(ref m_pDXGISwapChain1);

            SafeRelease(ref m_pDXGIDevice);
            SafeRelease(ref m_pD3D11DeviceContext);
            SafeRelease(ref m_pD2DDeviceContext);
            Marshal.Release(m_pD3D11DevicePtr);

            SafeRelease(ref m_pWICBitmapDecoder);
            SafeRelease(ref m_pD2DBitmapFrame);           
        }

        private void DisposeResources()
        {           
            SafeRelease(ref _pD2DBitmapCanvas);
            SafeRelease(ref _pD2DBitmapPreviousCanvas);

            if (_apng != null)
            {
                foreach (var f in _apng.Frames)
                {
                    SafeRelease(ref f.WICBitmap);                    
                }
            }
        }

        public void Dispose(bool disposing)
        {
            if (!bDisposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)

                    if (_stopWatch != null)
                    {
                        _stopWatch.Stop();
                        _stopWatch = null;
                    }
                }
                Clean();

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                bDisposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~WebPControl()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        void IDisposable.Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }

    class ApngFrame
    {
        public int Index;
        public int XOffset;
        public int YOffset;
        public int Width;
        public int Height;
        public ushort DelayNum;
        public ushort DelayDen;
        public byte DisposeOp;
        public byte BlendOp;

        public byte[] IhdrChunkData = Array.Empty<byte>();
        public List<PngChunk> GlobalChunks { get; } = new();
        public List<PngChunk> DataChunks { get; } = new();

        public IWICBitmap? WICBitmap;       
    }

    class PngChunk
    {
        public string Type = "";
        public byte[] Data = Array.Empty<byte>();
    }

    class ApngImage
    {
        public int CanvasWidth;
        public int CanvasHeight;
        public int NumPlays;
        public List<ApngFrame> Frames = new();
    }
}
