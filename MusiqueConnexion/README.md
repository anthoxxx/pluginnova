# MusiqueConnexion

Plugin Nova-Life : joue un **son du jeu** à chaque joueur quand il a fini de charger et
apparaît en jeu (connexion au serveur).

## Limite importante
L'API Nova-Life (1.69 – 1.69.1) ne permet **pas** de jouer une musique personnalisée
(fichier mp3, lien YouTube, webradio). La seule fonction son côté serveur est
`player.setup.TargetPlayClaironById(volume, index)`, qui joue un des **4 sons intégrés**
au jeu (les mêmes que le réglage `ticketAlertSound`, numéros 0 à 3).
L'écran de chargement lui-même est côté client : aucun plugin ne peut y mettre de son.

## Dépendances
Aucune (ni ModKit ni AAMenu).

## Compilation (Visual Studio)
1. Copier les 4 DLL listées dans `libs/README.md` dans le dossier `MusiqueConnexion/libs/`.
2. Ouvrir **`MusiqueConnexion.csproj`** directement.
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/MusiqueConnexion.dll` dans le dossier `Plugins` du serveur.

## Configuration
Au premier démarrage, `Plugins/MusiqueConnexion/config.json` est créé :

```json
{
  "Active": true,
  "IndexSon": 0,
  "Volume": 0.8,
  "DelaiSecondes": 2.0
}
```

- `IndexSon` : son du jeu à jouer, de 0 à 3.
- `Volume` : de 0 à 1.
- `DelaiSecondes` : attente après la fin du chargement avant de jouer le son.

## Commande (admin)
- `/musiqueconnexion <0-3> [volume]` (alias `/mc`) : écouter un son pour choisir le bon.
- `/musiqueconnexion reload` : recharger `config.json` sans redémarrer le serveur.
