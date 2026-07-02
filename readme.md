# Record Inventory App

[cite_start]A modern, fast, and keyboard-driven WPF desktop application designed to catalog and inventory vinyl record collections at lightning speed[cite: 358, 360]. [cite_start]Simply scan an album's barcode to instantly pull rich metadata from the Discogs API, automatically calculate valuation decisions, and log the results into a local database[cite: 355, 356, 592].

## 🚀 Features

* [cite_start]**Hands-Free Scanning:** Built as a keyboard-wedge system optimized for standalone USB hardware barcode scanners or wireless scanning companion apps[cite: 371, 628, 644].
* [cite_start]**Active Spotlight Dashboard:** Implements a sleek Windows 11 Dark Mode layout displaying high-resolution cover art and clear color-coded indicators (`KEEP IT`, `SELL IT`, `BARGAIN BIN`)[cite: 364, 365, 376].
* [cite_start]**Automatic Local Storage:** Instantly commits decoded album properties (Title, Artist, Release Year, Price, Rating) into a local SQLite database using high-performance Dapper queries[cite: 557, 592].
* [cite_start]**Interactive Session Log:** Displays a tabular, live-scrolling historical sidebar of past scans[cite: 366]. [cite_start]Clicking an entry re-displays details on the main dashboard without causing duplicate database records[cite: 771].
* [cite_start]**One-Click CSV Export:** Generates clean, properly formatted `.csv` spreadsheets compatible with Microsoft Excel or Google Sheets[cite: 558, 559].
* [cite_start]**Developer Sandboxing:** Built-in hidden command (`NUKEDB`) to safely drop database rows and flush UI states during testing[cite: 760, 764].

---

## 🛠️ Tech Stack & Requirements

* [cite_start]**Framework:** .NET 9.0 / WPF (Windows Presentation Foundation) [cite: 336, 351]
* [cite_start]**Database / Micro-ORM:** SQLite & Dapper [cite: 557]
* [cite_start]**Configuration Tracking:** Microsoft.Extensions.Configuration [cite: 14, 18]
* [cite_start]**Network Client:** HttpClient (Interfacing with the Discogs API v2 endpoint) [cite: 283, 707]

---

## 🔒 Security & Local Setup

[cite_start]To securely separate your private developer access keys from the public repository, this project manages configuration data via local environment mapping[cite: 3]. [cite_start]Follow these initialization steps to compile the application on your machine:

### 1. Generate Your Private API Token
1. [cite_start]Log into your account at [Discogs Developer Settings](https://www.discogs.com/settings/developers)[cite: 540].
2. [cite_start]Under the **Personal Access Token** section, click **Generate token**[cite: 548, 553].
3. [cite_start]Copy the unique alphanumeric string[cite: 555].

### 2. Set Up Local Configuration File
[cite_start]In the root directory of your project where the `.sln` solution file resides, create a text file named `appsettings.json`[cite: 7, 11]. [cite_start]Paste the following snippet, populating it with your newly generated token[cite: 8]:

```json
{
  "Discogs": {
    "ApiToken": "YOUR_PERSONAL_ACCESS_TOKEN_HERE"
  }
}