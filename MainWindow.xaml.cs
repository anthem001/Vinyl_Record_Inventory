using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
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
        public BitmapImage? AlbumArt { get; set; }
    }

    public record DiscogsResult(string Title, string Artist, string Year, double Rating, double MedianPrice, string CoverImageUrl, string Genre, string Style);

    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private static readonly string ConnectionString = $"Data Source={Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vinyl_inventory.db")}";
        private long _currentActiveDatabaseId = -1;
        private string _discogsToken = "";

        // Core Backing Fields
        private string _currentArtist = "Scan an album to begin...";
        private string _currentTitle = "---";
        private string _currentYear = "---";
        private string _currentGenre = "---";
        private string _currentStyle = "---";
        private double _currentRating = 0.0;
        private double _currentPrice = 0.00;
        private string _suggestedAction = "AWAITING SCAN";
        private BitmapImage? _currentAlbumArt;
        private Visibility _placeholderVisibility = Visibility.Visible;

        // Thread-Safe Public Properties Bindings
        public string CurrentArtist { get => _currentArtist; set { _currentArtist = value; OnPropertyChanged(); } }
        public string CurrentTitle { get => _currentTitle; set { _currentTitle = value; OnPropertyChanged(); } }
        public string CurrentYear { get => _currentYear; set { _currentYear = value; OnPropertyChanged(); } }
        public string CurrentGenre { get => _currentGenre; set { _currentGenre = value; OnPropertyChanged(); } }
        public string CurrentStyle { get => _currentStyle; set { _currentStyle = value; OnPropertyChanged(); } }
        public double CurrentRating { get => _currentRating; set { _currentRating = value; OnPropertyChanged(); OnPropertyChanged(nameof(RatingDisplayText)); } }
        public double CurrentPrice { get => _currentPrice; set { _currentPrice = value; OnPropertyChanged(); } }
        public string SuggestedAction { get => _suggestedAction; set { _suggestedAction = value; OnPropertyChanged(); } }
        public BitmapImage? CurrentAlbumArt { get => _currentAlbumArt; set { _currentAlbumArt = value; OnPropertyChanged(); } }
        public Visibility PlaceholderVisibility { get => _placeholderVisibility; set { _placeholderVisibility = value; OnPropertyChanged(); } }

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

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;

            // Load appsettings profile securely on boot
            LoadConfiguration();

            InitializeDatabase();
            LoadHistoricalSessionLog();

            this.Loaded += (s, e) => FocusInput();
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
                    Genre TEXT,
                    Style TEXT,
                    ScanDate DATETIME DEFAULT CURRENT_TIMESTAMP
                );";
            connection.Execute(createTableSql);

            try
            {
                connection.Execute("ALTER TABLE Inventory ADD COLUMN Genre TEXT;");
                connection.Execute("ALTER TABLE Inventory ADD COLUMN Style TEXT;");
            }
            catch { /* Columns exist safely */ }
        }

        private void LoadConfiguration()
        {
            try
            {
                var config = new ConfigurationBuilder()
                    .SetBasePath(AppDomain.CurrentDomain.BaseDirectory) // Forces the portable directory target
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    .Build();

                _discogsToken = config["Discogs:ApiToken"] ?? "";

                if (string.IsNullOrEmpty(_discogsToken))
                {
                    MessageBox.Show("Discogs API Token missing! Please place an appsettings.json file in this folder.",
                                    "Configuration Needed", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            catch { /* Fails silently if database configuration conflicts */ }
        }

        private void BarcodeTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string input = BarcodeTextBox.Text.Trim();

                BarcodeTextBox.Clear();
                e.Handled = true;

                if (!string.IsNullOrEmpty(input))
                {
                    if (input.Equals("NUKEDB", StringComparison.OrdinalIgnoreCase))
                    {
                        HandleDatabaseNuke();
                    }
                    else
                    {
                        // Completely non-blocking background threads execution
                        Task.Run(async () =>
                        {
                            await ProcessBarcodeAsync(input);
                        });
                    }
                }
            }
        }

        private void HandleDatabaseNuke()
        {
            MessageBoxResult result = MessageBox.Show(
                "This will delete the entire database. Are you sure?",
                "CRITICAL: Confirm Database Purge",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No
            );

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    using var connection = new SqliteConnection(ConnectionString);
                    connection.Execute("DELETE FROM Inventory;");
                    connection.Execute("DELETE FROM sqlite_sequence WHERE name='Inventory';");

                    Application.Current.Dispatcher.Invoke(() => ScannedHistory.Clear());

                    CurrentArtist = "Database Purged Completely.";
                    CurrentTitle = "---";
                    CurrentYear = "---";
                    CurrentGenre = "---";
                    CurrentStyle = "---";
                    CurrentRating = 0.0;
                    CurrentPrice = 0.00;
                    SuggestedAction = "AWAITING SCAN";
                    CurrentAlbumArt = null;
                    PlaceholderVisibility = Visibility.Visible;
                    _currentActiveDatabaseId = -1;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to clear database: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void LogList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.ListView listView && listView.SelectedItem is AlbumRecord selectedAlbum)
            {
                try
                {
                    using var connection = new SqliteConnection(ConnectionString);
                    var dbRecord = connection.QueryFirstOrDefault(
                        "SELECT * FROM Inventory WHERE Title = @Title AND Artist = @Artist ORDER BY ScanDate DESC",
                        new { Title = selectedAlbum.Title, Artist = selectedAlbum.Artist }
                    );

                    if (dbRecord != null)
                    {
                        CurrentTitle = dbRecord.Title?.ToString() ?? "";
                        CurrentArtist = dbRecord.Artist?.ToString() ?? "";
                        CurrentYear = dbRecord.ReleaseYear?.ToString() ?? "---";
                        CurrentGenre = dbRecord.Genre?.ToString() ?? "---";
                        CurrentStyle = dbRecord.Style?.ToString() ?? "---";
                        CurrentRating = dbRecord.Rating != null ? Convert.ToDouble(dbRecord.Rating) : 0.0;
                        CurrentPrice = dbRecord.EstimatedPrice != null ? Convert.ToDouble(dbRecord.EstimatedPrice) : 0.00;
                        SuggestedAction = dbRecord.Decision?.ToString() ?? "AWAITING SCAN";
                        _currentActiveDatabaseId = Convert.ToInt64(dbRecord.Id);

                        var searchData = await FetchFromDiscogsAsync(dbRecord.Barcode?.ToString() ?? "");

                        BitmapImage? historicalArt = null;
                        if (searchData != null && !string.IsNullOrEmpty(searchData.CoverImageUrl))
                        {
                            historicalArt = await DownloadImageAsync(searchData.CoverImageUrl);
                        }

                        CurrentAlbumArt = historicalArt;
                        PlaceholderVisibility = historicalArt != null ? Visibility.Collapsed : Visibility.Visible;
                    }
                }
                catch
                {
                    CurrentTitle = selectedAlbum.Title;
                    CurrentArtist = selectedAlbum.Artist;
                    CurrentPrice = selectedAlbum.Price;
                    SuggestedAction = selectedAlbum.Action;
                    CurrentGenre = "---";
                    CurrentStyle = "---";
                    CurrentAlbumArt = null;
                    PlaceholderVisibility = Visibility.Visible;
                    _currentActiveDatabaseId = -1;
                }
                finally
                {
                    listView.SelectedIndex = -1;
                    FocusInput();
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
                CurrentGenre = albumData.Genre;
                CurrentStyle = albumData.Style;

                if (CurrentPrice >= 20.00) SuggestedAction = "SELL IT";
                else if (CurrentRating >= 3.80) SuggestedAction = "KEEP IT";
                else SuggestedAction = "BARGAIN BIN";

                BitmapImage? downloadedArt = null;
                if (!string.IsNullOrEmpty(albumData.CoverImageUrl))
                {
                    downloadedArt = await DownloadImageAsync(albumData.CoverImageUrl);
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    CurrentAlbumArt = downloadedArt;
                    PlaceholderVisibility = downloadedArt != null ? Visibility.Collapsed : Visibility.Visible;

                    ScannedHistory.Insert(0, new AlbumRecord
                    {
                        Title = CurrentTitle,
                        Artist = CurrentArtist,
                        Price = CurrentPrice,
                        Action = SuggestedAction,
                        AlbumArt = downloadedArt,
                    });
                });

                _currentActiveDatabaseId = SaveToDatabase(barcode, CurrentArtist, CurrentTitle, CurrentYear, CurrentRating, CurrentPrice, SuggestedAction, CurrentGenre, CurrentStyle);
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    SuggestedAction = "ERROR";
                    CurrentTitle = ex.Message;
                    CurrentArtist = "Barcode Not Found";
                    CurrentYear = "---";
                    CurrentGenre = "---";
                    CurrentStyle = "---";
                    CurrentAlbumArt = null;
                    PlaceholderVisibility = Visibility.Visible;
                });
                _currentActiveDatabaseId = -1;
            }
            finally
            {
                FocusInput();
            }
        }

        private long SaveToDatabase(string barcode, string artist, string title, string year, double rating, double price, string decision, string genre, string style)
        {
            using var connection = new SqliteConnection(ConnectionString);
            string insertSql = @"
                INSERT INTO Inventory (Barcode, Artist, Title, ReleaseYear, Rating, EstimatedPrice, Decision, Genre, Style)
                VALUES (@Barcode, @Artist, @Title, @ReleaseYear, @Rating, @EstimatedPrice, @Decision, @Genre, @Style);
                SELECT last_insert_rowid();";

            return connection.ExecuteScalar<long>(insertSql, new
            {
                Barcode = barcode,
                Artist = artist,
                Title = title,
                ReleaseYear = year,
                Rating = rating,
                EstimatedPrice = price,
                Decision = decision,
                Genre = genre,
                Style = style
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
                    csvContent.AppendLine("Barcode,Artist,Title,Release Year,Genre,Style,Community Rating,Estimated Price,Decision,Scan Date");

                    foreach (var item in items)
                    {
                        string cleanArtist = $"\"{item.Artist?.ToString().Replace("\"", "\"\"")}\"";
                        string cleanTitle = $"\"{item.Title?.ToString().Replace("\"", "\"\"")}\"";
                        string cleanGenre = $"\"{item.Genre?.ToString().Replace("\"", "\"\"")}\"";
                        string cleanStyle = $"\"{item.Style?.ToString().Replace("\"", "\"\"")}\"";

                        csvContent.AppendLine($"{item.Barcode},{cleanArtist},{cleanTitle},{item.ReleaseYear},{cleanGenre},{cleanStyle},{item.Rating},{item.EstimatedPrice},{item.Decision},{item.ScanDate}");
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
                    Application.Current.Dispatcher.Invoke(() => ScannedHistory.Remove(visualItem));
                }

                CurrentArtist = "Record Removed Successfully.";
                CurrentTitle = "---";
                CurrentYear = "---";
                CurrentGenre = "---";
                CurrentStyle = "---";
                CurrentRating = 0.0;
                CurrentPrice = 0.00;
                SuggestedAction = "DELETED";
                CurrentAlbumArt = null;
                PlaceholderVisibility = Visibility.Visible;
                _currentActiveDatabaseId = -1;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to remove record: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void FocusInput()
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                BarcodeTextBox.Focus();
                Keyboard.Focus(BarcodeTextBox);
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private async Task<DiscogsResult> FetchFromDiscogsAsync(string barcode)
        {
            // FIX: Append the token directly into the URL query string as a parameter!
            // This completely bypasses the need for custom Header Authorization objects.
            string url = $"https://api.discogs.com/database/search?barcode={barcode}&token={_discogsToken}";

            using (var apiClient = new HttpClient())
            {
                // Discogs still strictly requires a User-Agent so they know it's a valid app request
                apiClient.DefaultRequestHeaders.Add("User-Agent", "VinylSorterApp/1.0 (contact@yourdomain.com)");

                HttpResponseMessage response = await apiClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    throw new Exception($"Discogs Server Response Error: {response.StatusCode}");

                string jsonString = await response.Content.ReadAsStringAsync();

                using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(jsonString))
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                    {
                        throw new Exception("Barcode variant not recognized in Discogs directory database.");
                    }

                    System.Text.Json.JsonElement activeTargetResult = results[0];
                    string coverUrl = "";

                    foreach (System.Text.Json.JsonElement tempResult in results.EnumerateArray())
                    {
                        string tempUrl = "";

                        if (tempResult.TryGetProperty("cover_image", out var imgProp))
                        {
                            tempUrl = imgProp.GetString() ?? "";
                        }

                        if (tempUrl.Contains("spacer.gif") || tempUrl.Contains("pixel.gif"))
                        {
                            tempUrl = "";
                        }

                        if (string.IsNullOrEmpty(tempUrl))
                        {
                            if (tempResult.TryGetProperty("thumb", out var thumbProp))
                            {
                                tempUrl = thumbProp.GetString() ?? "";
                            }
                        }

                        if (tempUrl.Contains("spacer.gif") || tempUrl.Contains("pixel.gif"))
                        {
                            tempUrl = "";
                        }

                        if (!string.IsNullOrEmpty(tempUrl))
                        {
                            activeTargetResult = tempResult;
                            coverUrl = tempUrl;
                            break;
                        }
                    }

                    var selectedItem = activeTargetResult;

                    string fullTitle = "Unknown - Unknown";
                    if (selectedItem.TryGetProperty("title", out var titleProp))
                    {
                        fullTitle = titleProp.GetString() ?? "Unknown - Unknown";
                    }

                    string[] parts = fullTitle.Split(new[] { " - " }, 2, StringSplitOptions.None);
                    string artist = parts.Length > 0 ? parts[0] : "Unknown";
                    string title = parts.Length > 1 ? parts[1] : "Unknown";

                    string year = "---";
                    if (selectedItem.TryGetProperty("year", out var yProp))
                    {
                        year = yProp.GetString() ?? "---";
                    }

                    string genre = "---";
                    if (selectedItem.TryGetProperty("genre", out var gProp) && gProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        genre = string.Join(", ", gProp.EnumerateArray().Select(x => x.GetString()));
                    }

                    string style = "---";
                    if (selectedItem.TryGetProperty("style", out var sProp) && sProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        style = string.Join(", ", sProp.EnumerateArray().Select(x => x.GetString()));
                    }

                    return new DiscogsResult(title, artist, year, 4.15, 18.50, coverUrl, genre, style);
                }
            }
        }

        private async Task<BitmapImage?> DownloadImageAsync(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;

            try
            {
                using (var cleanClient = new HttpClient())
                {
                    cleanClient.DefaultRequestHeaders.Add("User-Agent", "VinylSorterApp/1.0 (contact@yourdomain.com)");

                    byte[] imageBytes = await cleanClient.GetByteArrayAsync(url);
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
            }
            catch
            {
                return null;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}