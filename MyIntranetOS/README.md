# MyIntranet OS – plugin Nova Life + tablette web

## Compiler la DLL
1. Copier les 6 DLL listées dans `libs/README.md` dans `libs/`.
2. `dotnet build -c Release` → `bin/Release/net472/MyIntranetOS.dll` (+ le dossier `MyIntranetOS/` copié à côté).

## Installation
1. Mettre `MyIntranetOS.dll` dans le dossier plugins du serveur (ModKit requis).
2. Copier le dossier `MyIntranetOS/` (Web, Application(s), Configuration, Data) à côté de la DLL.
3. Éditer `Configuration/configuration.json` (port libre, `publicUrl` = IP/domaine joignable par les joueurs, ouvrir le port dans le pare-feu).
   Sous Windows sans droits admin, le serveur web n'écoute qu'en local : `netsh http add urlacl url=http://*:8080/ user=Tout le monde`.
4. Démarrer le serveur : consulter les logs `[MyIntranetOS]`.

## Identification du joueur
- `/tablette` en jeu -> lien personnel avec cookie de session (recommandé), ou
- automatique par l'IP de sa session de jeu (`identifyByIp`) ; refusé si plusieurs joueurs ont la même IP.
- `devSteamId` pour tester hors jeu (**à laisser vide en production** : sinon n'importe qui est identifié comme ce joueur).

## Sauvegarde temps réel
`Data/mios.json` est réécrit à chaque modification (réglages, code, notifications), de façon atomique, avec `mios.json.bak`.
Contenu : réglages par joueur, code (hash+sel), tentatives/blocages, notifications, actualités.

## Depuis vos autres plugins
`MyIntranetAPI.Notify(steamId, "Titre", "Texte");`
`MyIntranetAPI.PublishNews("MAIRIE", "Titre", "Texte");`

## Apps
Dossier `Application(s)/<nom>/` avec `app.json` + `index.html`. `/api/me` est accessible aux apps ; `parent.postMessage({type:'close'},'*')` ferme l'app.

## API Nova Life utilisée
`Nova.server.OnMinutePassedEvent`, `OnPlayerSpawnCharacter`, `OnPlayerDisconnect`, `SChatCommand`,
`player.steamId`, `player.FullName`, `player.character.Bank`, `player.SendText`, `NetworkConnectionToClient.address` (Mirror).
Noms vérifiés sur les DLL du jeu (Nova-Life 1.69).
