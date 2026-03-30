using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using CatchmentTool.Services;

namespace CatchmentTool.UI
{
    /// <summary>
    /// Interactive map dialog for selecting a site location and fetching
    /// the 2-year, 24-hour rainfall depth (P₂) from NOAA Atlas 14.
    ///
    /// Uses WebView2 + Leaflet/OpenStreetMap for the map.
    /// </summary>
    public partial class RainfallMapDialog : Window
    {
        /// <summary>
        /// The P₂ rainfall depth (inches) selected by the user.
        /// Set after the user clicks OK. -1 if cancelled.
        /// </summary>
        public double P2RainfallInches { get; private set; } = -1;

        /// <summary>Selected latitude (WGS84).</summary>
        public double SelectedLatitude { get; private set; } = double.NaN;

        /// <summary>Selected longitude (WGS84).</summary>
        public double SelectedLongitude { get; private set; } = double.NaN;

        private bool _webViewReady = false;
        private bool _fetching = false;

        public RainfallMapDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                txtStatus.Text = "Loading map...";

                // Initialize WebView2
                var env = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), "CatchmentTool_WebView2"));

                await mapWebView.EnsureCoreWebView2Async(env);

                // Listen for messages from JavaScript
                mapWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                // Load the Leaflet map
                mapWebView.CoreWebView2.NavigateToString(GetMapHtml());
                _webViewReady = true;
                txtStatus.Text = "Click the map to select your site location.";
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Map failed to load: {ex.Message}";
                MessageBox.Show(
                    "WebView2 could not be initialized. You can enter P₂ manually.\n\n" +
                    $"Error: {ex.Message}",
                    "Map Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Handle messages from JavaScript (map click coordinates).
        /// </summary>
        private async void OnWebMessageReceived(object sender,
            CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(message)) return;

                // Expected format: "clicked:lat,lng"
                if (message.StartsWith("clicked:"))
                {
                    string coords = message.Substring(8);
                    string[] parts = coords.Split(',');
                    if (parts.Length != 2) return;

                    if (!double.TryParse(parts[0], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double lat)) return;
                    if (!double.TryParse(parts[1], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double lng)) return;

                    SelectedLatitude = lat;
                    SelectedLongitude = lng;

                    // Update UI
                    txtLatitude.Text = lat.ToString("F5");
                    txtLongitude.Text = lng.ToString("F5");
                    txtP2Value.Text = "...";
                    txtP2Unit.Text = "";
                    txtStatus.Text = "Fetching from NOAA Atlas 14...";
                    btnOk.IsEnabled = false;

                    // Fetch P2 from NOAA
                    await FetchP2Async(lat, lng);
                }
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Fetch P₂ from NOAA Atlas 14 and update the UI.
        /// </summary>
        private async Task FetchP2Async(double lat, double lng)
        {
            if (_fetching) return;
            _fetching = true;

            try
            {
                var result = await NoaaAtlas14Service.FetchP2Async(lat, lng);

                if (result.Success)
                {
                    P2RainfallInches = result.P2_24hr;
                    txtP2Value.Text = result.P2_24hr.ToString("F2");
                    txtP2Unit.Text = "inches";
                    txtManualP2.Text = result.P2_24hr.ToString("F2");
                    txtStatus.Text = $"NOAA Atlas 14: P₂ = {result.P2_24hr:F2} inches";
                    btnOk.IsEnabled = true;

                    // Update the map popup
                    if (_webViewReady)
                    {
                        string js = $"updatePopup({lat.ToString(CultureInfo.InvariantCulture)}, " +
                                    $"{lng.ToString(CultureInfo.InvariantCulture)}, " +
                                    $"{result.P2_24hr.ToString("F2", CultureInfo.InvariantCulture)});";
                        await mapWebView.CoreWebView2.ExecuteScriptAsync(js);
                    }
                }
                else
                {
                    txtP2Value.Text = "N/A";
                    txtP2Unit.Text = "";
                    txtStatus.Text = result.Error;

                    // Still enable OK if user can type a manual value
                    if (!string.IsNullOrWhiteSpace(txtManualP2.Text))
                        btnOk.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                txtP2Value.Text = "Error";
                txtStatus.Text = $"Fetch failed: {ex.Message}";
            }
            finally
            {
                _fetching = false;
            }
        }

        /// <summary>
        /// Manual P₂ override — user can type a value directly.
        /// </summary>
        private void TxtManualP2_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(txtManualP2.Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double val) && val > 0)
            {
                P2RainfallInches = val;
                btnOk.IsEnabled = true;
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            // Final check — use manual override if present
            if (double.TryParse(txtManualP2.Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double manual) && manual > 0)
            {
                P2RainfallInches = manual;
            }

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            P2RainfallInches = -1;
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// Generate the inline HTML for the Leaflet map.
        /// Uses CDN-hosted Leaflet + OpenStreetMap tiles.
        /// </summary>
        private string GetMapHtml()
        {
            return @"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'/>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'/>
    <link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css'/>
    <script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
    <style>
        * { margin: 0; padding: 0; }
        html, body { height: 100%; }
        #map { height: 100%; width: 100%; }
        .info-box {
            background: white;
            padding: 8px 12px;
            border-radius: 4px;
            box-shadow: 0 1px 5px rgba(0,0,0,0.3);
            font-family: 'Segoe UI', sans-serif;
            font-size: 12px;
            line-height: 1.5;
        }
        .info-box b { color: #0078D4; }
    </style>
</head>
<body>
    <div id='map'></div>
    <script>
        // Initialize map centered on CONUS
        var map = L.map('map').setView([39.0, -98.0], 4);

        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            attribution: '&copy; OpenStreetMap contributors',
            maxZoom: 19
        }).addTo(map);

        var marker = null;
        var popup = null;

        // Custom pin icon
        var pinIcon = L.divIcon({
            className: '',
            html: '<svg width=""30"" height=""40"" viewBox=""0 0 30 40""><path d=""M15 0C6.7 0 0 6.7 0 15c0 10.5 15 25 15 25s15-14.5 15-25C30 6.7 23.3 0 15 0z"" fill=""#0078D4""/><circle cx=""15"" cy=""14"" r=""6"" fill=""white""/></svg>',
            iconSize: [30, 40],
            iconAnchor: [15, 40],
            popupAnchor: [0, -35]
        });

        // Click handler
        map.on('click', function(e) {
            var lat = e.latlng.lat;
            var lng = e.latlng.lng;

            // Place or move marker
            if (marker) {
                marker.setLatLng(e.latlng);
            } else {
                marker = L.marker(e.latlng, {icon: pinIcon}).addTo(map);
            }

            // Show loading popup
            marker.bindPopup('<div class=""info-box"">Fetching P&#x2082; from NOAA...</div>').openPopup();

            // Send coordinates to C#
            window.chrome.webview.postMessage('clicked:' + lat.toFixed(6) + ',' + lng.toFixed(6));
        });

        // Called from C# to update the popup with the P2 result
        function updatePopup(lat, lng, p2) {
            if (marker) {
                marker.bindPopup(
                    '<div class=""info-box"">' +
                    '<b>P&#x2082; = ' + p2 + ' inches</b><br/>' +
                    '2-year, 24-hour rainfall<br/>' +
                    '<span style=""color:#666"">' + lat.toFixed(4) + ', ' + lng.toFixed(4) + '</span>' +
                    '</div>'
                ).openPopup();
            }
        }

        // Called from C# to center the map on a location
        function centerMap(lat, lng, zoom) {
            map.setView([lat, lng], zoom || 12);
        }
    </script>
</body>
</html>";
        }
    }
}
