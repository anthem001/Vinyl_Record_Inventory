using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Record_Inventory
{
    

    public class AlbumRecord
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public double Price { get; set; }
        public string Action { get; set; } = "";
    }

    public record DiscogsResult(string Title, string Artist, string Year, double Rating, double MedianPrice, string CoverImageUrl);

    public partial class MainWindow : Window
    {
        private const string ConnectionString = "Data Source=vinyl_inventory.db";
        private long _currentActiveDatabaseId = -1;

        public string CurrentArtist { get; set; } = "Scan an album to begin...";
        public string CurrentTitle { get; set; } = "---";
        public string CurrentYear { get; set; } = "---";
        public double CurrentRating { get; set; } = 0.0;
        public double CurrentPrice { get; set; } = 0.00;
        public string SuggestedAction { get; set; } = "AWAITING SCAN";
        public BitmapImage? CurrentAlbumArt { get; set; }

        private string _discogsToken;

        // Dynamic formatting text property computed directly for your XAML display card
        public string RatingDisplayText
        {
            get
            {
                if (CurrentRating <= 0.0) return "---";

                string classification;
                if (CurrentRating >= 4.5) classification = "Masterpiece";
                else if (CurrentRating >= 4.0) classification = "Great";
                else if (CurrentRating >= 3.0) classification = "Good / Average";
                else if (CurrentRating >= 2.0) classification = "Poor";
                else classification = "Abysmal";

                return $"{CurrentRating:F2} / 5\n({classification})";
            }
        }

        public ObservableCollection<AlbumRecord> ScannedHistory { get; } = new();
        private readonly HttpClient _httpClient;

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "VinylSorterApp/1.0 (contact@yourdomain.com)");
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Discogs", _discogsToken);

            InitializeDatabase();
            LoadHistoricalSessionLog();
            BarcodeTextBox.Focus();
        }

        private void InitializeDatabase()
        {
            using var connection = new SqliteConnection(ConnectionString);
            string createTableSql = @"
                CREATE TABLE IF NOT EXISTS Inventory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Barcode TEXT,
                    Artist TEXT,
                    Title TEXT,
                    ReleaseYear TEXT,
                    Rating REAL,
                    EstimatedPrice REAL,
                    Decision TEXT,
                    ScanDate DATETIME DEFAULT CURRENT_TIMESTAMP
                );";
            connection.Execute(createTableSql);
        }

        private void LoadConfiguration()
        {
            try
            {
                var config = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    //.AddEnvironmentVariables() // Allows advanced users to pass it via OS environment variables
                    .Build();

                _discogsToken = config["Discogs:ApiToken"];

                if (string.IsNullOrEmpty(_discogsToken))
                {
                    MessageBox.Show("Discogs API Token missing! Please create an appsettings.json file or set a 'Discogs__ApiToken' environment variable.", "Configuration Needed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load configuration: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadHistoricalSessionLog()
        {
            try
            {
                using var connection = new SqliteConnection(ConnectionString);
                var items = connection.Query("SELECT Title, Artist, EstimatedPrice as Price, Decision as Action FROM Inventory ORDER BY ScanDate DESC LIMIT 50");
                foreach (var item in items)
                {
                    ScannedHistory.Add(new AlbumRecord
                    {
                        Title = item.Title?.ToString() ?? "",
                        Artist = item.Artist?.ToString() ?? "",
                        Price = item.Price != null ? Convert.ToDouble(item.Price) : 0.0,
                        Action = item.Action?.ToString() ?? ""
                    });
                }
            }
            catch { /* Fails silently if database is locked */ }
        }

        private async void BarcodeTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string input = BarcodeTextBox.Text.Trim();

                if (!string.IsNullOrEmpty(input))
                {
                    // Check for our secret developer factory-reset command
                    if (input.Equals("NUKEDB", StringComparison.OrdinalIgnoreCase))
                    {
                        HandleDatabaseNuke();
                    }
                    else
                    {
                        // Proceed with regular vinyl lookup
                        await ProcessBarcodeAsync(input);
                    }
                }

                BarcodeTextBox.Clear();
                BarcodeTextBox.Focus();
                e.Handled = true;
            }
        }

        private void HandleDatabaseNuke()
        {
            // 1. Fire the warning prompt dialog box
            MessageBoxResult result = MessageBox.Show(
                "This will delete the entire database. Are you sure?",
                "CRITICAL: Confirm Database Purge",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No // Default button focus is safely set to "No"
            );

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    using var connection = new SqliteConnection(ConnectionString);

                    // Wipe out all records inside the table
                    connection.Execute("DELETE FROM Inventory;");

                    // Optional: Resets the SQLite auto-increment counter back to 1
                    connection.Execute("DELETE FROM sqlite_sequence WHERE name='Inventory';");

                    // Clear the right-hand visual sidebar log immediately
                    ScannedHistory.Clear();

                    // Reset the spotlight display panel view back to pristine state
                    CurrentArtist = "Database Purged Completely.";
                    CurrentTitle = "---";
                    CurrentYear = "---";
                    CurrentRating = 0.0;
                    CurrentPrice = 0.00;
                    SuggestedAction = "AWAITING SCAN";
                    CurrentAlbumArt = null;
                    _currentActiveDatabaseId = -1;

                    this.DataContext = null;
                    this.DataContext = this;

                    MessageBox.Show("All database tables have been successfully cleared.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to clear database: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void LogList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // 1. Ensure the user actually selected a valid row item
            if (sender is System.Windows.Controls.ListView listView && listView.SelectedItem is AlbumRecord selectedAlbum)
            {
                try
                {
                    using var connection = new SqliteConnection(ConnectionString);

                    // 2. Query the deep metadata from your local SQLite storage using the unique matching Title & Artist
                    var dbRecord = connection.QueryFirstOrDefault(
                        "SELECT * FROM Inventory WHERE Title = @Title AND Artist = @Artist ORDER BY ScanDate DESC",
                        new { Title = selectedAlbum.Title, Artist = selectedAlbum.Artist }
                    );

                    if (dbRecord != null)
                    {
                        // 3. Re-populate the spotlight display fields smoothly
                        CurrentTitle = dbRecord.Title?.ToString() ?? "";
                        CurrentArtist = dbRecord.Artist?.ToString() ?? "";
                        CurrentYear = dbRecord.ReleaseYear?.ToString() ?? "---";
                        CurrentRating = dbRecord.Rating != null ? Convert.ToDouble(dbRecord.Rating) : 0.0;
                        CurrentPrice = dbRecord.EstimatedPrice != null ? Convert.ToDouble(dbRecord.EstimatedPrice) : 0.00;
                        SuggestedAction = dbRecord.Decision?.ToString() ?? "AWAITING SCAN";

                        // Track the true database row ID so your "Delete Active" button still points to the correct entry!
                        _currentActiveDatabaseId = Convert.ToInt64(dbRecord.Id);

                        // 4. Safely pull a fresh copy of the cover art over the API network or leave it clear if missing
                        var searchData = await FetchFromDiscogsAsync(dbRecord.Barcode?.ToString() ?? "");
                        if (searchData != null && !string.IsNullOrEmpty(searchData.CoverImageUrl))
                        {
                            CurrentAlbumArt = await DownloadImageAsync(searchData.CoverImageUrl);
                        }
                        else
                        {
                            CurrentAlbumArt = null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Fallback to basic view if network image download fails
                    CurrentTitle = selectedAlbum.Title;
                    CurrentArtist = selectedAlbum.Artist;
                    CurrentPrice = selectedAlbum.Price;
                    SuggestedAction = selectedAlbum.Action;
                    _currentActiveDatabaseId = -1;
                }
                finally
                {
                    // 5. Instantly force the WPF layout matrix to redraw and reflect updates on-screen
                    RefreshUI();

                    // 6. Clear out the list selection style so it's ready to be clicked again later
                    listView.SelectedIndex = -1;
                }
            }
        }

        public async Task ProcessBarcodeAsync(string barcode)
        {
            try
            {
                var albumData = await FetchFromDiscogsAsync(barcode);

                CurrentTitle = albumData.Title;
                CurrentArtist = albumData.Artist;
                CurrentYear = albumData.Year;
                CurrentRating = albumData.Rating;
                CurrentPrice = albumData.MedianPrice;

                // Threshold Evaluator (Using 3.80 instead of 4.20 to safe-keep split reviews)
                if (CurrentPrice >= 20.00) SuggestedAction = "SELL IT";
                else if (CurrentRating >= 3.80) SuggestedAction = "KEEP IT";
                else SuggestedAction = "BARGAIN BIN";

                if (!string.IsNullOrEmpty(albumData.CoverImageUrl))
                {
                    CurrentAlbumArt = await DownloadImageAsync(albumData.CoverImageUrl);
                }

                _currentActiveDatabaseId = SaveToDatabase(barcode, CurrentArtist, CurrentTitle, CurrentYear, CurrentRating, CurrentPrice, SuggestedAction);

                ScannedHistory.Insert(0, new AlbumRecord
                {
                    Title = CurrentTitle,
                    Artist = CurrentArtist,
                    Price = CurrentPrice,
                    Action = SuggestedAction
                });

                RefreshUI();
            }
            catch (Exception ex)
            {
                SuggestedAction = "ERROR";
                CurrentTitle = ex.Message;
                _currentActiveDatabaseId = -1;
                RefreshUI();
            }
        }

        private long SaveToDatabase(string barcode, string artist, string title, string year, double rating, double price, string decision)
        {
            using var connection = new SqliteConnection(ConnectionString);
            string insertSql = @"
                INSERT INTO Inventory (Barcode, Artist, Title, ReleaseYear, Rating, EstimatedPrice, Decision)
                VALUES (@Barcode, @Artist, @Title, @ReleaseYear, @Rating, @EstimatedPrice, @Decision);
                SELECT last_insert_rowid();";

            return connection.ExecuteScalar<long>(insertSql, new
            {
                Barcode = barcode,
                Artist = artist,
                Title = title,
                ReleaseYear = year,
                Rating = rating,
                EstimatedPrice = price,
                Decision = decision
            });
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                Filter = "CSV Spreadsheet (*.csv)|*.csv",
                FileName = $"Vinyl_Inventory_Export_{DateTime.Now:yyyyMMdd}.csv"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    using var connection = new SqliteConnection(ConnectionString);
                    var items = connection.Query("SELECT * FROM Inventory ORDER BY ScanDate DESC");

                    StringBuilder csvContent = new StringBuilder();
                    csvContent.AppendLine("Barcode,Artist,Title,Release Year,Community Rating,Estimated Price,Decision,Scan Date");

                    foreach (var item in items)
                    {
                        string cleanArtist = $"\"{item.Artist?.ToString().Replace("\"", "\"\"")}\"";
                        string cleanTitle = $"\"{item.Title?.ToString().Replace("\"", "\"\"")}\"";

                        csvContent.AppendLine($"{item.Barcode},{cleanArtist},{cleanTitle},{item.ReleaseYear},{item.Rating},{item.EstimatedPrice},{item.Decision},{item.ScanDate}");
                    }

                    File.WriteAllText(saveFileDialog.FileName, csvContent.ToString(), Encoding.UTF8);
                    MessageBox.Show("Inventory successfully exported to spreadsheet!", "Export Completed", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentActiveDatabaseId == -1)
            {
                MessageBox.Show("No active album record loaded to remove.", "Action Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Execute("DELETE FROM Inventory WHERE Id = @Id", new { Id = _currentActiveDatabaseId });

                var visualItem = ScannedHistory.FirstOrDefault(x => x.Title == CurrentTitle && x.Artist == CurrentArtist);
                if (visualItem != null)
                {
                    ScannedHistory.Remove(visualItem);
                }

                CurrentArtist = "Record Removed Successfully.";
                CurrentTitle = "---";
                CurrentYear = "---";
                CurrentRating = 0.0;
                CurrentPrice = 0.00;
                SuggestedAction = "DELETED";
                CurrentAlbumArt = null;
                _currentActiveDatabaseId = -1;

                RefreshUI();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to remove record: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshUI()
        {
            this.DataContext = null;
            this.DataContext = this;
        }

        private async Task<DiscogsResult> FetchFromDiscogsAsync(string barcode)
        {
            string url = $"https://api.discogs.com/database/search?barcode={barcode}";
            HttpResponseMessage response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Discogs Web Client Error: {response.StatusCode}");

            string jsonString = await response.Content.ReadAsStringAsync();

            using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(jsonString))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                {
                    var firstResult = results[0];
                    string fullTitle = firstResult.GetProperty("title").GetString() ?? "Unknown - Unknown";
                    string[] parts = fullTitle.Split(new[] { " - " }, 2, StringSplitOptions.None);
                    string artist = parts.Length > 0 ? parts[0] : "Unknown";
                    string title = parts.Length > 1 ? parts[1] : "Unknown";

                    string year = firstResult.TryGetProperty("year", out var yProp) ? yProp.GetString() ?? "---" : "---";
                    string coverUrl = firstResult.TryGetProperty("cover_image", out var imgProp) ? imgProp.GetString() ?? "" : "";

                    // Real live data mapping. (Temporary rating mock stays at a standard 4.15 for testing)
                    return new DiscogsResult(title, artist, year, 4.15, 18.50, coverUrl);
                }
            }
            throw new Exception("No matching vinyl records discovered under this barcode parameter.");
        }

        private async Task<BitmapImage?> DownloadImageAsync(string url)
        {
            try
            {
                byte[] imageBytes = await _httpClient.GetByteArrayAsync(url);
                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(imageBytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                return bitmap;
            }
            catch { return null; }
        }
    }
}