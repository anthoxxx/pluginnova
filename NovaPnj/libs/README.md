# DLL nécessaires à la compilation

Mettez ces fichiers **directement dans ce dossier `libs/`** (ils ne sont pas envoyés sur GitHub) :

| Fichier | Où le trouver |
|---|---|
| `Assembly-CSharp.dll` | dossier du serveur Nova-Life → `Nova-Life_Data/Managed/` |
| `Mirror.dll` | même dossier |
| `UnityEngine.CoreModule.dll` | même dossier |
| `UnityEngine.PhysicsModule.dll` | même dossier |
| `Newtonsoft.Json.dll` | même dossier |

Pas besoin de ModKit ni d'AAMenu : NovaPnj est un plugin autonome.

S'il en manque une, la compilation affiche : `DLL manquante : XXX.dll`.
Autre dossier possible : `dotnet build -c Release -p:LibsPath="C:\chemin\vers\dlls"`
