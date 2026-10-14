# Eusebius
Program which sends a link to multiple archive sites at once.

Setup tutorial
1. Open up the main folder where Program.cs is in a terminal.
2. Run the command: 'dotnet build' and wait for it to complete
3. Run the command: 'pwsh bin/Debug/net10.0/playwright.ps1 install chromium' and wait for it to complete
4. Test it with a csample website, Wikipedia works the best.

Currently supported:
* Archive.today
* Arquivo.pt
* Ghost Archive
* Wayback Machine

Features be implemented:
* Ad hoc archiving with SingleFile and filesharing websites (Catbox, Pone.rs)
* Archiving multiple websites in one run via .txt file
* Megalodon.jp and Perma.cc website support
* Outbound link archiving like with the Wayback Machine
