using System;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CatchmentTool.Services
{
    /// <summary>
    /// Fetches precipitation frequency data from the NOAA Precipitation
    /// Frequency Data Server (PFDS / Atlas 14).
    ///
    /// Used to retrieve the 2-year, 24-hour rainfall depth (P₂) for
    /// TR-55 Time of Concentration sheet flow calculations.
    /// </summary>
    public class NoaaAtlas14Service
    {
        private static readonly HttpClient _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        /// <summary>
        /// NOAA PFDS endpoint for precipitation frequency estimates.
        /// Returns quantile data for all durations and return periods.
        /// </summary>
        private const string PfdsUrl =
            "https://hdsc.nws.noaa.gov/cgi-bin/hdsc/new/cgi_readH5.py" +
            "?lat={0}&lon={1}&type=pf&data=depth&units=english&series=pds";

        /// <summary>
        /// Result of a NOAA Atlas 14 query.
        /// </summary>
        public class Atlas14Result
        {
            /// <summary>P₂: 2-year, 24-hour precipitation depth (inches).</summary>
            public double P2_24hr { get; set; }

            /// <summary>Location queried (latitude).</summary>
            public double Latitude { get; set; }

            /// <summary>Location queried (longitude).</summary>
            public double Longitude { get; set; }

            /// <summary>True if the query succeeded.</summary>
            public bool Success { get; set; }

            /// <summary>Error message if the query failed.</summary>
            public string Error { get; set; }

            /// <summary>Raw server response (for debugging).</summary>
            public string RawResponse { get; set; }
        }

        /// <summary>
        /// Fetch the 2-year, 24-hour rainfall depth from NOAA Atlas 14.
        /// </summary>
        /// <param name="latitude">Latitude in decimal degrees (WGS84).</param>
        /// <param name="longitude">Longitude in decimal degrees (WGS84, negative for west).</param>
        /// <returns>Atlas14Result with the P₂ value.</returns>
        public static async Task<Atlas14Result> FetchP2Async(double latitude, double longitude)
        {
            var result = new Atlas14Result
            {
                Latitude = latitude,
                Longitude = longitude
            };

            try
            {
                string url = string.Format(
                    CultureInfo.InvariantCulture, PfdsUrl, latitude, longitude);

                string response = await _client.GetStringAsync(url);
                result.RawResponse = response;

                // Parse the quantiles array from the response.
                // Format: quantiles = [[v,v,...],[v,v,...],...]
                // Rows = durations (index 9 = 24-hour)
                // Cols = return periods (index 1 = 2-year)
                double p2 = ParseP2FromResponse(response);

                if (p2 > 0)
                {
                    result.P2_24hr = p2;
                    result.Success = true;
                }
                else
                {
                    result.Error = "Could not parse P₂ from NOAA response. " +
                                   "Location may be outside NOAA Atlas 14 coverage.";
                }
            }
            catch (HttpRequestException ex)
            {
                result.Error = $"Network error: {ex.Message}";
            }
            catch (TaskCanceledException)
            {
                result.Error = "Request timed out (30s). Check internet connection.";
            }
            catch (Exception ex)
            {
                result.Error = $"Unexpected error: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// Parse the 2-year, 24-hour precipitation depth from the PFDS response.
        ///
        /// The response contains a JavaScript-like line:
        ///   quantiles = [[...],[...],...]
        ///
        /// Duration index 9 = 24-hour, Return period index 1 = 2-year.
        /// </summary>
        private static double ParseP2FromResponse(string response)
        {
            // Find the quantiles line
            var match = Regex.Match(response, @"quantiles\s*=\s*\[(.+)\]",
                RegexOptions.Singleline);

            if (!match.Success)
                return -1;

            string arrayContent = match.Groups[1].Value;

            // Parse the nested arrays: [[a,b,c],[d,e,f],...]
            // We need row index 9 (24-hour) and column index 1 (2-year)
            var rowMatches = Regex.Matches(arrayContent, @"\[([^\[\]]+)\]");

            // Duration indices (0-based):
            //  0=5min, 1=10min, 2=15min, 3=30min, 4=60min,
            //  5=2hr, 6=3hr, 7=6hr, 8=12hr, 9=24hr,
            //  10=2day, 11=3day, 12=4day, 13=7day, 14=10day,
            //  15=20day, 16=30day, 17=45day, 18=60day
            const int durationIndex24Hr = 9;

            // Return period indices (0-based):
            //  0=1yr, 1=2yr, 2=5yr, 3=10yr, 4=25yr,
            //  5=50yr, 6=100yr, 7=200yr, 8=500yr, 9=1000yr
            const int returnPeriod2Yr = 1;

            if (rowMatches.Count <= durationIndex24Hr)
                return -1;

            string row24hr = rowMatches[durationIndex24Hr].Groups[1].Value;
            string[] values = row24hr.Split(',');

            if (values.Length <= returnPeriod2Yr)
                return -1;

            string valueStr = values[returnPeriod2Yr].Trim().Trim('"');

            if (double.TryParse(valueStr, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double p2))
            {
                return p2;
            }

            return -1;
        }
    }
}
