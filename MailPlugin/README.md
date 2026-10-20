# MailPlugin

Messagerie entre joueurs (par matheo mercier) : boîte mail, envoi à des joueurs hors ligne,
administration (consulter / vider les boîtes, bloquer un utilisateur).

- Menu interaction AAMenu « Boite mail », menu admin AAMenu « Messagerie », commande `/mail`.
- Données : `MailPlugin_data.json` dans le dossier du plugin.

## Compiler la DLL

1. Copier les 7 DLL dans `MailPlugin/libs/` (voir `libs/README.md`).
2. `dotnet build MailPlugin -c Release`
3. Déposer `MailPlugin/bin/Release/net472/MailPlugin.dll` dans le dossier `Plugins` du serveur
   (avec ModKit et AAMenu).
