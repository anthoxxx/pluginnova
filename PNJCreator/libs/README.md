# DLL nécessaires à la compilation

Mettez ces 5 fichiers **directement dans ce dossier `libs/`** (ils ne sont pas envoyés sur GitHub).
Ils se trouvent tous dans le dossier du serveur Nova-Life → `Nova-Life_Data/Managed/` (ou `..._Data/Managed/`) :

| Fichier |
|---|
| `Assembly-CSharp.dll` |
| `Assembly-CSharp-firstpass.dll` |
| `Mirror.dll` |
| `UnityEngine.CoreModule.dll` |
| `Newtonsoft.Json.dll` |

Pas besoin de ModKit, AAMenu ni Harmony : le plugin n'utilise que les DLL du jeu.

S'il en manque une, la compilation affiche : `DLL manquante : XXX.dll`.

Autre dossier possible : `dotnet build -p:LibsPath="C:\chemin\vers\dlls"`
