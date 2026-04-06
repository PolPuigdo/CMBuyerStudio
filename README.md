# CMBuyerStudio

> Smart card search and wishlist management for **Magic: The Gathering** buyers on **Cardmarket**.

<p align="center">
  <strong>Search faster.</strong> • <strong>Compare variants.</strong> • <strong>Build your wanted list.</strong>
</p>

---

## 📚 Table of Contents

- [✨ Overview](#-overview)
- [🚀 What You Can Do](#-what-you-can-do)
- [🧭 How It Works](#-how-it-works)
- [🖥️ Main Screens](#️-main-screens)
- [💾 Where Your Data Is Stored](#-where-your-data-is-stored)
- [📋 Requirements](#-requirements)
- [▶️ Getting Started](#️-getting-started)
- [⚠️ Current Limitations](#️-current-limitations)
- [🛣️ Current Project Status](#️-current-project-status)
- [❤️ Why This Project Is Useful](#️-why-this-project-is-useful)

---

## ✨ Overview

**CMBuyerStudio** is a Windows desktop app built for people who buy **Magic: The Gathering singles** on **Cardmarket** and want a cleaner, faster way to prepare their purchases.

Instead of manually juggling searches, set names, prices, and acceptable versions, the app helps you:

- 🔎 search a card directly on Cardmarket;
- 🃏 review available variants;
- 💶 compare prices at a glance;
- ✅ choose the versions you actually want;
- 📦 assign a desired quantity;
- 💾 save everything into a persistent wanted list.

In short: **it helps you organize what you want to buy before making your final purchase decisions.**

---

## 🚀 What You Can Do

### ✅ Search cards on Cardmarket

Type a card name and the app fetches matching **Magic singles** from Cardmarket.

Each result can show:

- **Card name**
- **Set / expansion**
- **Price**
- **Preview image**
- **Selection checkbox**

Results are automatically sorted by **price**, which makes comparison faster and more practical.

### ✅ Select the variants you actually accept

Not every version of a card is equally interesting. Sometimes you only want:

- a specific set;
- the cheapest acceptable versions;
- a few selected printings;
- a more flexible pool of options before buying.

CMBuyerStudio lets you mark one or multiple variants and save only the ones you care about.

### ✅ Build a persistent wanted list

After selecting the variants you want, you can set a **desired quantity** and save them into your personal list.

If you save the same card again:

- the quantity is **added** to the existing total;
- new variants are **merged in**;
- duplicate variants are **not repeated**.

### ✅ Manage your saved wanted cards

Inside the **Wanted Cards** section, you can:

- edit desired quantities;
- remove individual variants;
- delete an entire card group;
- clear the whole wanted list.

All changes are saved automatically, so your list stays available between sessions.

---

## 🧭 How It Works

The current recommended flow is very simple:

1. Open the app.
2. Go to **Search**.
3. Enter a card name.
4. Review the results.
5. Select the versions you would be willing to buy.
6. Choose the quantity you want.
7. Click **Save Selection**.
8. Open **Wanted Cards** to review and refine your list.

This is the **main working flow of the project today**.

---

## 🖥️ Main Screens

### 🔍 Search

This is the most important screen right now.

Here you can:

- search a card by name;
- browse found variants;
- select one, many, or all results;
- save selected variants with a desired quantity.

### 🃏 Wanted Cards

This is your saved buying list.

Here you can:

- review saved card groups;
- update quantities;
- remove unwanted variants;
- delete full groups;
- clear the full list if needed.

### ▶️ Run

Currently visible in the UI, but **not implemented yet**.

### ⚙️ Settings

Currently visible in the UI, but **not implemented yet**.

### 📄 Logs

Currently visible in the UI, but **not implemented yet**.

---

## 💾 Where Your Data Is Stored

The app stores its local data inside:

```text
%LOCALAPPDATA%\CMBuyerStudio
```

Important files and folders:

- `cards.json` → your saved wanted list
- `Cache\CardsImages` → downloaded card images
- `Reports` → reserved for future features
- `Logs` → reserved for future features

This means your saved list remains on your computer even after closing the app.

---

## 📋 Requirements

For end users, the practical requirements are:

- **Windows**
- **Internet connection**
- **Access to Cardmarket**

Because the search depends on Cardmarket data, changes on Cardmarket's side may affect search behavior in the future.

---

## ▶️ Getting Started

### Best option for end users

If the repository provides a **Release**, the easiest path is:

1. Download the published version from GitHub Releases.
2. Launch the application.
3. Start using **Search** and **Wanted Cards**.

### If there is no Release yet

The project is still in an early phase. In that case, running it may require launching it from the source code, which is more technical and less convenient for non-technical users.

---

## ⚠️ Current Limitations

To keep expectations realistic, here are the current limits of the app:

- it currently focuses on **Magic: The Gathering singles** on Cardmarket;
- the core implemented flow is **searching, selecting, and saving wanted cards**;
- **Run**, **Settings**, and **Logs** are still placeholders;
- the app does **not** automatically buy cards for you;
- functionality may be affected if Cardmarket changes its page structure.

---

## 🛣️ Current Project Status

### What is already working

- ✅ Card search
- ✅ Variant selection
- ✅ Quantity selection
- ✅ Wanted list persistence
- ✅ Editing saved groups
- ✅ Removing variants and groups

### What is still in progress

- 🚧 Run workflow
- 🚧 Settings screen
- 🚧 Logs screen
- 🚧 More advanced buying/reporting features

---

## ❤️ Why This Project Is Useful

Buying cards often means balancing:

- different printings;
- different prices;
- acceptable sets;
- quantities you still need.

**CMBuyerStudio turns that messy manual process into a clearer workflow.**

Instead of repeatedly searching the same cards and remembering which versions were acceptable, you can keep a structured wanted list and come back to it whenever you need.

---

## Final Summary

If your goal is to **search cards, compare versions, and maintain a practical wanted list for Cardmarket**, CMBuyerStudio already delivers value today.

It is especially useful if you want a tool that feels more focused on **buying preparation** than on raw browsing.
