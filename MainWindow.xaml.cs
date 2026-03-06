
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;

using GlobalStructures;
using static GlobalStructures.GlobalTools;
using Direct2D;
using WIC;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUI3_APNG
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private IntPtr hWndMain = IntPtr.Zero;

        ID2D1Factory m_pD2DFactory = null;
        ID2D1Factory1 m_pD2DFactory1 = null;
        IWICImagingFactory m_pWICImagingFactory = null;

        public MainWindow()
        {
            InitializeComponent();
            hWndMain = WinRT.Interop.WindowNative.GetWindowHandle(this);
            this.Title = "WinUI 3 - APNG control";

            m_pWICImagingFactory = (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(WICTools.CLSID_WICImagingFactory));
            HRESULT hr = CreateD2D1Factory();

            APNGC1.Init(hWndMain, m_pD2DFactory1, m_pWICImagingFactory);
            string sExePath = AppContext.BaseDirectory;
            string sFilePath = sExePath + @"/Assets/Dollars.png";
            APNGC1.LoadFile(sFilePath, tbWidth, tbHeight, tbAnimation);

            this.Closed += MainWindow_Closed;

        }

        private async void btnBrowse_Click(object sender, RoutedEventArgs e)
        {
            string sFilePath = await OpenFileDialog();
            if (sFilePath != string.Empty)
            {
                if (!APNGC1.LoadFile(sFilePath, tbWidth, tbHeight, tbAnimation))
                {
                    Windows.UI.Popups.MessageDialog md = new Windows.UI.Popups.MessageDialog(sFilePath + " does not seem to be a PNG file !", "Information");
                    WinRT.Interop.InitializeWithWindow.Initialize(md, hWndMain);
                    _ = await md.ShowAsync();
                }
            }
        }

        private async Task<string> OpenFileDialog()
        {
            var fop = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(fop, hWndMain);
            fop.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;

            fop.FileTypeFilter.Add(".png");

            var file = await fop.PickSingleFileAsync();
            return (file != null ? file.Path : string.Empty);
        }

        public HRESULT CreateD2D1Factory()
        {
            HRESULT hr = HRESULT.S_OK;
            D2D1_FACTORY_OPTIONS options = new D2D1_FACTORY_OPTIONS();

            // Needs "Enable native code Debugging"
#if DEBUG
            options.debugLevel = D2D1_DEBUG_LEVEL.D2D1_DEBUG_LEVEL_INFORMATION;
#endif

            hr = D2DTools.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED, ref D2DTools.CLSID_D2D1Factory, ref options, out m_pD2DFactory);
            m_pD2DFactory1 = (ID2D1Factory1)m_pD2DFactory;
            return hr;
        }

        void Clean()
        {
            SafeRelease(ref m_pWICImagingFactory);
            SafeRelease(ref m_pD2DFactory1);
            SafeRelease(ref m_pD2DFactory);
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            APNGC1.Dispose(true);           
            Clean();
        }
    }
}
