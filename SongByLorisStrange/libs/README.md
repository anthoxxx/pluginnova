# DLL nécessaires à la compilation

Mettez ces 7 fichiers **directement dans ce dossier `libs/`** (ils ne sont pas envoyés sur GitHub).
Les 5 premiers sont dans le dossier du serveur Nova-Life → `Nova-Life_Data/Managed/` (ou `..._Data/Managed/`) :

- `Assembly-CSharp.dll`
- `Assembly-CSharp-firstpass.dll`
- `Mirror.dll`
- `UnityEngine.CoreModule.dll`
- `Newtonsoft.Json.dll`
- `ModKit.dll` (le plugin ModKit, renommé exactement `ModKit.dll`)
- `AAMenu.dll` (le plugin AAMenu, renommé exactement `AAMenu.dll`)

S'il en manque une, la compilation affiche : `DLL manquante : XXX.dll`.

Autre dossier possible : `dotnet build -p:LibsPath="C:\chemin\vers\dlls"`
