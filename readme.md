# Record Inventory App

A modern, fast, and keyboard-driven WPF desktop application designed to catalog and inventory vinyl record collections at lightning speed. Simply scan an album's barcode to instantly pull rich metadata from the Discogs API, automatically calculate valuation decisions, and log the results into a local database.

## 🚀 Features

* **Hands-Free Scanning:** Built as a keyboard-wedge system optimized for standalone USB hardware barcode scanners or wireless scanning companion apps.
* **Active Spotlight Dashboard:** Implements a Windows 11 Dark Mode layout displaying high-resolution cover art and clear color-coded indicators (`KEEP IT`, `SELL IT`, `BARGAIN BIN`).
* **Automatic Local Storage:** Instantly commits decoded album properties (Title, Artist, Release Year, Price, Rating) into a local SQLite database using high-performance Dapper queries.
* **Interactive Session Log:** Displays a tabular, live-scrolling historical sidebar of past scans. Clicking an entry re-displays details on the main dashboard without causing duplicate database records.
* **One-Click CSV Export:** Generates clean, properly formatted `.csv` spreadsheets compatible with Microsoft Excel or Google Sheets.
* **Developer Sandboxing:** Built-in hidden command (`NUKEDB`) to safely drop database rows and flush UI states during testing.

---

## 🛠️ Tech Stack & Requirements

* **Framework:** .NET 9.0 / WPF (Windows Presentation Foundation)
* **Database / Micro-ORM:** SQLite & Dapper
* **Configuration Tracking:** Microsoft.Extensions.Configuration
* **Network Client:** HttpClient (Interfacing with the Discogs API v2 endpoint)

---

## 🔒 Security & Local Setup

To securely separate your private developer access keys from the public repository, this project manages configuration data via local environment mapping. Follow these initialization steps to compile the application on your machine:

### 1. Generate Your Private API Token
1. Log into your account at [Discogs Developer Settings](https://www.discogs.com/settings/developers).
2. Under the **Personal Access Token** section, click **Generate token**.
3. Copy the unique alphanumeric string.

### 2. Set Up Local Configuration File
In the root directory of your project where the `.sln` solution file resides, create a text file named `appsettings.json`. Paste the following snippet, populating it with your newly generated token:

```json
{
  "Discogs": {
    "ApiToken": "YOUR_PERSONAL_ACCESS_TOKEN_HERE"
  }
}
```
### 3. Roadmap
1. Will publish a version soon for easy download
2. Add Genre and Style from discogs
3. Remove delete action button as redundant