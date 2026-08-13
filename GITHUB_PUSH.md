# First push to GitHub (source)

Clean public source for **Zomboid Manager v1.0.8** (includes polished README + screenshots).

## Push (GitHub Desktop or CLI)

```powershell
cd "C:\Users\mgpp1\Desktop\Zomboid Manager\github-source-upload"
git init
git add .
git commit --trailer "Co-authored-by: Cursor <cursoragent@cursor.com>" -m "Public source and README for Zomboid Manager 1.0.8"
git branch -M main
git remote add origin https://github.com/Vuscoo/zomboidmanager.git
git push -u origin main
```

If the repo already has history, use GitHub Desktop: open this folder, commit, push to `Vuscoo/zomboidmanager`.

After push, the GitHub home page shows the new README with screenshots.
Releases (EXE) stay separate: tag `v1.0.8` + upload `ZomboidManager.exe`.
