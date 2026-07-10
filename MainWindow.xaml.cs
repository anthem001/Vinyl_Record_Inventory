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

    public record DiscogsResult(string Title, string Artist, string Year, double Rating, double MedianPrice, string CoverImageUrl, string Genre, string Style, string Id);

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

        public async Task ProcessBarcodeAsync(string input)
        {
            try
            {
                // Simple regex check: if it contains non-digits, treat it as an Artist/Catalog keyword search
                bool isTextSearch = input.Any(c => !char.IsDigit(c));

                List<DiscogsResult> searchResults = await FetchFromDiscogsAsync(input, isTextSearch);
                DiscogsResult targetAlbum = null;

                if (isTextSearch)
                {
                    // If text search returned multiple records, launch the selection popup dialog box
                    targetAlbum = PromptUserForSelection(searchResults);
                    if (targetAlbum == null) return; // User aborted the popup operation
                }
                else
                {
                    // Barcode match returns index zero straight out of the array context
                    targetAlbum = searchResults[0];
                }

                // Display properties update block
                CurrentTitle = targetAlbum.Title;
                CurrentArtist = targetAlbum.Artist;
                CurrentYear = targetAlbum.Year;
                CurrentRating = targetAlbum.Rating;
                CurrentPrice = targetAlbum.MedianPrice;
                CurrentGenre = targetAlbum.Genre;
                CurrentStyle = targetAlbum.Style;

                if (CurrentPrice >= 20.00) SuggestedAction = "SELL IT";
                else if (CurrentRating >= 3.80) SuggestedAction = "KEEP IT";
                else SuggestedAction = "BARGAIN BIN";

                if (!string.IsNullOrEmpty(targetAlbum.CoverImageUrl))
                {
                    CurrentAlbumArt = await DownloadImageAsync(targetAlbum.CoverImageUrl);
                }
                else
                {
                    CurrentAlbumArt = null;
                }

                // Commit to database file automatically
                string codeIdentifier = isTextSearch ? $"MANUAL_ID_{targetAlbum.Id}" : input;
                _currentActiveDatabaseId = SaveToDatabase(codeIdentifier, CurrentArtist, CurrentTitle, CurrentYear, CurrentRating, CurrentPrice, SuggestedAction, CurrentGenre, CurrentStyle);

                // Hand modification down smoothly via the dispatcher queue layout thread
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ScannedHistory.Insert(0, new AlbumRecord
                    {
                        Title = CurrentTitle,
                        Artist = CurrentArtist,
                        Price = CurrentPrice,
                        Action = SuggestedAction
                    });
                });

                RefreshUI();
            }
            catch (Exception ex)
            {
                SuggestedAction = "ERROR";
                CurrentTitle = ex.Message;
                CurrentArtist = "Search Aborted";
                CurrentGenre = "---";
                CurrentStyle = "---";
                _currentActiveDatabaseId = -1;
                RefreshUI();
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

        private async Task<List<DiscogsResult>> FetchFromDiscogsAsync(string input, bool isTextSearch = false)
        {
            var resultsList = new List<List<DiscogsResult>>();
            // Dynamically build the endpoint based on query parameters
            string url = isTextSearch
                ? $"https://api.discogs.com/database/search?q={Uri.EscapeDataString(input)}&type=release&token={_discogsToken}"
                : $"https://api.discogs.com/database/search?barcode={input}&token={_discogsToken}";

            using var apiClient = new HttpClient();
            apiClient.DefaultRequestHeaders.Add("User-Agent", "VinylSorterApp/1.0 (contact@yourdomain.com)");

            HttpResponseMessage response = await apiClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Discogs Web Client Error: {response.StatusCode}");

            string jsonString = await response.Content.ReadAsStringAsync();
            var foundItems = new List<DiscogsResult>();

            using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(jsonString))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                {
                    // If it's a barcode scan, we only want the top match. If it's text, grab up to 5 options.
                    int itemsToTake = isTextSearch ? Math.Min(5, results.GetArrayLength()) : 1;

                    for (int i = 0; i < itemsToTake; i++)
                    {
                        var item = results[i];
                        string id = item.TryGetProperty("id", out var idProp) ? idProp.GetInt64().ToString() : "";
                        string fullTitle = item.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "Unknown - Unknown" : "Unknown - Unknown";

                        string[] parts = fullTitle.Split(new[] { " - " }, 2, StringSplitOptions.None);
                        string artist = parts.Length > 0 ? parts[0] : "Unknown";
                        string title = parts.Length > 1 ? parts[1] : "Unknown";

                        string year = item.TryGetProperty("year", out var yProp) ? yProp.GetString() ?? "---" : "---";

                        // Fallback extraction system for image resources
                        string coverUrl = "";
                        if (item.TryGetProperty("cover_image", out var imgProp) && !string.IsNullOrEmpty(imgProp.GetString()) && !imgProp.GetString().Contains("spacer.gif"))
                            coverUrl = imgProp.GetString()!;
                        else if (item.TryGetProperty("thumb", out var thumbProp) && !string.IsNullOrEmpty(thumbProp.GetString()) && !thumbProp.GetString().Contains("spacer.gif"))
                            coverUrl = thumbProp.GetString()!;

                        string genre = "---";
                        if (item.TryGetProperty("genre", out var gProp) && gProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                            genre = string.Join(", ", gProp.EnumerateArray().Select(x => x.GetString()));

                        string style = "---";
                        if (item.TryGetProperty("style", out var sProp) && sProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                            style = string.Join(", ", sProp.EnumerateArray().Select(x => x.GetString()));

                        // Standard mock values for calculation engines
                        foundItems.Add(new DiscogsResult(title, artist, year, 4.15, 18.50, coverUrl, genre, style, id));
                    }
                }
            }

            if (foundItems.Count == 0)
                throw new Exception("No matching vinyl records discovered under these parameters.");

            return foundItems;
        }

        private DiscogsResult PromptUserForSelection(List<DiscogsResult> choices)
        {
            DiscogsResult selectedChoice = null;

            // Create a dynamic selection popup window container shell
            Window popup = new Window
            {
                Title = "Select Album Pressing Match",
                Width = 450,
                Height = 320,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E1E1E")),
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize
            };

            System.Windows.Controls.Grid mainGrid = new System.Windows.Controls.Grid();
            mainGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            mainGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

            System.Windows.Controls.TextBlock titleTxt = new System.Windows.Controls.TextBlock
            {
                Text = "Multiple entries found. Select the precise album model:",
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(15, 15, 15, 5),
                FontSize = 14,
                FontWeight = FontWeights.Bold
            };
            System.Windows.Controls.Grid.SetRow(titleTxt, 0);
            mainGrid.Children.Add(titleTxt);

            System.Windows.Controls.ListBox listBox = new System.Windows.Controls.ListBox
            {
                Margin = new Thickness(15, 5, 15, 10),
                Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2D2D2D")),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0)
            };

            foreach (var choice in choices)
            {
                listBox.Items.Add($"{choice.Artist} - {choice.Title} [{choice.Year}]");
            }
            listBox.SelectedIndex = 0; // Default selection fallback
            System.Windows.Controls.Grid.SetRow(listBox, 1);
            mainGrid.Children.Add(listBox);

            System.Windows.Controls.Button confirmBtn = new System.Windows.Controls.Button
            {
                Content = "Add Selected Album to Inventory",
                Height = 35,
                Margin = new Thickness(15, 0, 15, 15),
                Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#007ACC")),
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(0)
            };

            confirmBtn.Click += (s, e) =>
            {
                if (listBox.SelectedIndex >= 0)
                {
                    selectedChoice = choices[listBox.SelectedIndex];
                }
                popup.DialogResult = true;
                popup.Close();
            };
            System.Windows.Controls.Grid.SetRow(confirmBtn, 2);
            mainGrid.Children.Add(confirmBtn);

            popup.Content = mainGrid;

            // If user explicitly closes out or hits cancel, return null to cancel tracking safely
            bool? result = popup.ShowDialog();
            return result == true ? selectedChoice : null;
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

        private void RefreshUI()
        {
            // Force the WPF layout engine to redraw and update the UI fields
            this.DataContext = null;
            this.DataContext = this;

            // Safely shift focus back into the input box so you can scan continuously
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                BarcodeTextBox.Focus();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private async void BtnManualSearch_Click(object sender, RoutedEventArgs e)
        {
            // Launch the window and pass our active authorization token down
            var searchWindow = new ManualSearchWindow(_discogsToken);
            searchWindow.Owner = this;

            if (searchWindow.ShowDialog() == true && searchWindow.SelectedRecord != null)
            {
                var targetAlbum = searchWindow.SelectedRecord;

                // Push values to the Spotlight view panels
                CurrentTitle = targetAlbum.Title;
                CurrentArtist = targetAlbum.Artist;
                CurrentYear = targetAlbum.Year;
                CurrentRating = targetAlbum.Rating;
                CurrentPrice = targetAlbum.MedianPrice;
                CurrentGenre = targetAlbum.Genre;
                CurrentStyle = targetAlbum.Style;

                // Calculate decision tracking labels
                if (CurrentPrice >= 20.00) SuggestedAction = "SELL IT";
                else if (CurrentRating >= 3.80) SuggestedAction = "KEEP IT";
                else SuggestedAction = "BARGAIN BIN";

                // Download the artwork if a link is provided
                if (!string.IsNullOrEmpty(targetAlbum.CoverImageUrl))
                {
                    CurrentAlbumArt = await DownloadImageAsync(targetAlbum.CoverImageUrl);
                }
                else
                {
                    CurrentAlbumArt = null;
                }

                // Generate a placeholder "Manual ID" since there's no barcode sequence text string
                string randomManualId = $"MANUAL_{DateTime.Now.Ticks}";

                // Save straight to the SQLite file
                _currentActiveDatabaseId = SaveToDatabase(randomManualId, CurrentArtist, CurrentTitle, CurrentYear, CurrentRating, CurrentPrice, SuggestedAction, CurrentGenre, CurrentStyle);

                // Prepend seamlessly to your local Session Log sidebar
                ScannedHistory.Insert(0, new AlbumRecord
                {
                    Title = CurrentTitle,
                    Artist = CurrentArtist,
                    Price = CurrentPrice,
                    Action = SuggestedAction
                });

                // Redraw and shift cursor back safely
                RefreshUI();
            }
        }
    }
}