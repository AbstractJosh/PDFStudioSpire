using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PdfiumViewer;                 // from PdfiumViewer.Updated
using Spire.Pdf;                    // Spire.PDF core
using Spire.Pdf.Graphics;           // Spire drawing
using Point = System.Windows.Point;

namespace PDFStudioSpire
{
    public partial class MainWindow : Window
    {
        // Viewer bits
        private PdfViewer _pdfiumViewer;          // WinForms control
        private PdfDocument _pdfiumDoc;           // Pdfium document (dispose on reload)
        private double _zoom = 1.0;

        // Editing engine – source of truth
        private Spire.Pdf.PdfDocument _spireDoc;  // Spire in-memory doc
        private int _currentPageIndex = 0;        // simple: always page 0 for demo; hook to page change later

        // Debounce timer for “real-time” commit -> reload
        private readonly DispatcherTimer _applyReloadDebounce;

        // Pending commit action (set by edits)
        private Action _pendingCommit;

        // Add-text mode flag
        private bool _isAddTextMode = false;

        public MainWindow()
        {
            InitializeComponent();

            // Init Pdfium viewer inside the host
            _pdfiumViewer = new PdfViewer
            {
                ShowToolbar = false,
                ShowBookmarks = false,
                ZoomMode = PdfViewerZoomMode.FitBest
            };
            ((WindowsFormsHost)PdfHost).Child = _pdfiumViewer;

            // Overlay on top of viewer
            OverlayCanvas.IsHitTestVisible = false;

            // Debouncer
            _applyReloadDebounce = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _applyReloadDebounce.Tick += (s, e) =>
            {
                _applyReloadDebounce.Stop();
                _pendingCommit?.Invoke();
                _pendingCommit = null;
            };

            // Resize overlay to match host size
            SizeChanged += (_, __) => ResizeOverlayToHost();
            Loaded += (_, __) => ResizeOverlayToHost();
        }

        #region Toolbar actions

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "PDF files (*.pdf)|*.pdf",
                Title = "Open PDF"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                // Load into Spire as the edit source
                _spireDoc?.Close();
                _spireDoc = new Spire.Pdf.PdfDocument();
                _spireDoc.LoadFromFile(dlg.FileName);

                _currentPageIndex = 0;

                // Push to viewer
                ReloadViewerFromSpire();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to open PDF:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_spireDoc == null)
            {
                MessageBox.Show(this, "Open a PDF first.", "Info");
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PDF files (*.pdf)|*.pdf",
                Title = "Save As",
                FileName = "Edited.pdf"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                _spireDoc.SaveToFile(dlg.FileName);
                MessageBox.Show(this, "Saved.", "PDFStudioSpire");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Save failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            _zoom = Math.Min(6.0, _zoom + 0.1);
            _pdfiumViewer.Zoom = _zoom;
            ResizeOverlayToHost();
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            _zoom = Math.Max(0.2, _zoom - 0.1);
            _pdfiumViewer.Zoom = _zoom;
            ResizeOverlayToHost();
        }

        private void AddTextToggle_Checked(object sender, RoutedEventArgs e)
        {
            _isAddTextMode = true;
            OverlayCanvas.IsHitTestVisible = true;
            Mouse.OverrideCursor = Cursors.IBeam;
        }

        private void AddTextToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            _isAddTextMode = false;
            OverlayCanvas.IsHitTestVisible = false;
            Mouse.OverrideCursor = null;
        }

        #endregion

        #region Overlay & Editing

        private void OverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isAddTextMode || _spireDoc == null) return;

            var click = e.GetPosition(OverlayCanvas);

            // Create an inline TextBox where the user clicked
            var tb = new TextBox
            {
                MinWidth = 80,
                FontSize = 16,
                Background = new SolidColorBrush(Color.FromArgb(180, 30, 30, 30)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(4),
            };
            Canvas.SetLeft(tb, click.X);
            Canvas.SetTop(tb, click.Y);
            OverlayCanvas.Children.Add(tb);
            tb.Focus();

            // Commit when user presses Enter
            tb.KeyDown += (s, ke) =>
            {
                if (ke.Key == Key.Enter)
                {
                    var text = tb.Text?.Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        // Prepare a commit op (Spire draw string on current page)
                        QueueCommit(() => CommitAddTextToSpire(text, click));
                    }

                    OverlayCanvas.Children.Remove(tb);
                    ke.Handled = true;
                }
                else if (ke.Key == Key.Escape)
                {
                    OverlayCanvas.Children.Remove(tb);
                    ke.Handled = true;
                }
            };
        }

        private void QueueCommit(Action commitAction)
        {
            // Coalesce multiple quick edits
            _pendingCommit = commitAction;
            _applyReloadDebounce.Stop();
            _applyReloadDebounce.Start();
        }

        private void CommitAddTextToSpire(string text, Point overlayPoint)
        {
            if (_spireDoc == null) return;

            try
            {
                var page = _spireDoc.Pages[_currentPageIndex];

                // Convert overlay (WPF) Y-down to PDF Y-up
                // Assume 1 CSS px ~ 1 device px and current zoom; refine if you add DPI transforms.
                double pageHeight = page.Size.Height;
                var pdfX = overlayPoint.X / _zoom; // approximate: viewer scales with zoom
                var pdfY = pageHeight - (overlayPoint.Y / _zoom);

                var font = new PdfTrueTypeFont(new System.Drawing.Font("Arial", 12f), true);
                var brush = PdfBrushes.Black;
                page.Canvas.DrawString(text, font, brush, (float)pdfX, (float)pdfY);

                // After modifying Spire doc, hot-reload viewer
                ReloadViewerFromSpire();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Edit failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Viewer reload

        private void ReloadViewerFromSpire()
        {
            if (_spireDoc == null) return;

            // Save Spire doc into memory and feed to Pdfium
            using var ms = new MemoryStream();
            _spireDoc.SaveToStream(ms);
            ms.Position = 0;

            // Dispose previous Pdfium document
            _pdfiumDoc?.Dispose();
            _pdfiumDoc = PdfDocument.Load(ms);

            _pdfiumViewer.Document = _pdfiumDoc;
            _pdfiumViewer.Zoom = _zoom;
            _pdfiumViewer.Renderer.ScrollToPage(_currentPageIndex);
            ResizeOverlayToHost();
        }

        #endregion

        #region Helpers

        private void ResizeOverlayToHost()
        {
            // Match overlay pixel size to host area
            if (PdfHost.ActualWidth <= 0 || PdfHost.ActualHeight <= 0) return;
            OverlayCanvas.Width = PdfHost.ActualWidth;
            OverlayCanvas.Height = PdfHost.ActualHeight;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _pdfiumDoc?.Dispose();
            _spireDoc?.Close();
        }

        #endregion
    }
}
