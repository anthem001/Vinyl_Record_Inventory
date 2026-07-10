using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Record_Inventory
{
    /// <summary>
    /// Interaction logic for ManualSearchWindow.xaml
    /// </summary>
    public partial class ManualSearchWindow : Window
    {
        private readonly string _token;
        public DiscogsResult SelectedRecord { get; private set; }

        public class ManualSearchItem
        {
            public string DisplayTitle { get; set; }
            public DiscogsResult RawResult { get; set; }
        }

        public ManualSearchWindow(string discogsToken)
        {
            InitializeComponent();
            _token = discogsToken;
        }

        private async void BtnQuery_Click(object sender, RoutedEventArgs e)
        {
            string catalog = TxtCatalog.Text.Trim();
            string artist = TxtArtist.Text.Trim();

            if (string.IsNullOrEmpty(catalog))
            {
                MessageBox.Show("Please enter a catalog number or title query.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                LstResults.Items.Clear();
                BtnAddAlbum.IsEnabled = false;

                // Dynamically stitch the search string together
                string queryStr = string.IsNullOrEmpty(artist) ? catalog : $"{artist} {catalog}";
                string url = $"https://api.discogs.com/database/search?q={Uri.EscapeDataString(queryStr)}&type=release&token={_token}";

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "VinylSorterApp/1.0 (contact@yourdomain.com)");

                HttpResponseMessage response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode) throw new Exception($"Discogs Error: {response.StatusCode}");

                string json = await response.Content.ReadAsStringAsync();

                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                    {
                        int limit = Math.Min(5, results.GetArrayLength());
                        for (int i = 0; i < limit; i++)
                        {
                            var item = results[i];
                            string id = item.TryGetProperty("id", out var idProp) ? idProp.GetInt64().ToString() : "";
                            string fullTitle = item.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "Unknown" : "Unknown";
                            string year = item.TryGetProperty("year", out var yProp) ? yProp.GetString() ?? "---" : "---";

                            string coverUrl = "";
                            if (item.TryGetProperty("cover_image", out var imgProp) && !imgProp.GetString().Contains("spacer.gif"))
                                coverUrl = imgProp.GetString();

                            string genre = item.TryGetProperty("genre", out var gProp) && gProp.ValueKind == JsonValueKind.Array
                                ? string.Join(", ", gProp.EnumerateArray().Select(x => x.GetString())) : "---";

                            string style = item.TryGetProperty("style", out var sProp) && sProp.ValueKind == JsonValueKind.Array
                                ? string.Join(", ", sProp.EnumerateArray().Select(x => x.GetString())) : "---";

                            string[] parts = fullTitle.Split(new[] { " - " }, 2, StringSplitOptions.None);
                            string itemArtist = parts.Length > 0 ? parts[0] : "Unknown";
                            string itemTitle = parts.Length > 1 ? parts[1] : "Unknown";

                            var entry = new DiscogsResult(itemTitle, itemArtist, year, 4.20, 15.00, coverUrl, genre, style, id);

                            LstResults.Items.Add(new ManualSearchItem
                            {
                                DisplayTitle = $"{itemArtist} - {itemTitle} [{year}] (Cat: {catalog})",
                                RawResult = entry
                            });
                        }

                        LstResults.SelectedIndex = 0;
                        BtnAddAlbum.IsEnabled = true;
                    }
                    else
                    {
                        MessageBox.Show("No matching database entries were discovered.", "Zero Results", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Search failed: {ex.Message}", "API Exception", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAddAlbum_Click(object sender, RoutedEventArgs e)
        {
            if (LstResults.SelectedItem is ManualSearchItem selected)
            {
                // Confirmation dialog prompt before final structural check-in
                var result = MessageBox.Show($"Are you sure you want to add '{selected.RawResult.Artist} - {selected.RawResult.Title}' to your database?", "Confirm Import", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    SelectedRecord = selected.RawResult;
                    this.DialogResult = true;
                    this.Close();
                }
            }
        }
    }
}
