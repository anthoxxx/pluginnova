# MyIntranet OS

Une vraie tablette intranet pour les joueurs de **Nova-Life: Amboise**, accessible depuis le navigateur du PC
en jeu (**F2**). Écran de verrouillage, widgets, dock, centre de contrôle et applications installables.

Chaque joueur a sa propre tablette, liée à son **compte Steam et à son personnage**. Les préférences sont
sauvegardées dans une base **SQLite**. Le plugin héberge son propre serveur web : **aucune dépendance externe**
(ni ModKit, ni AAMenu, ni serveur web à installer).

## Fonctionnalités

- 🖥️ Écran d'accueil, widgets (horloge, identité, portefeuille, calendrier), dock (4 apps), centre de contrôle.
- 🔐 Identification automatique du joueur (adresse IP de sa connexion au serveur de jeu) ; à défaut, code
  d'association à taper dans le chat. La session se ferme quand le joueur quitte le serveur.
- 🎨 Thème clair/sombre/auto, couleur d'accent, 8 fonds d'écran ou image personnalisée, luminosité, taille du texte.
- 🔒 Code de verrouillage à 4 chiffres (haché PBKDF2), verrouillage automatique, protection anti-force-brute
  (blocage progressif par compte + limite par IP).
- 📱 Applications : un dossier dans `Applications/` avec un `app.json` et une page HTML. Détection à chaud.
- 🔌 API JSON (`/api/me`, `/api/apps`, `/api/settings`, `/api/storage`) et SDK `postMessage` pour les apps.
- 💾 Tout est persistant dans `MyIntranetOS/myintranet.db`.

## Installation

1. Copiez `MyIntranetOS.dll` dans le dossier des plugins de votre serveur.
2. Démarrez le serveur une première fois : le dossier `MyIntranetOS/` est créé à côté de la DLL :
   ```
   MyIntranetOS/
   ├── Configuration/configuration.json
   ├── Applications/reglages/      (application intégrée)
   ├── DOCUMENTATION.md            (créer ses applications)
   └── myintranet.db
   ```
3. Dans `Configuration/configuration.json`, choisissez un **port libre** (`Port`) et indiquez l'adresse publique
   de la tablette (`PublicUrl`, ex. `http://51.75.12.34:8090/`). **Ouvrez ce port en TCP** dans le pare-feu de la
   machine / de l'hébergeur.
4. Redémarrez le serveur et vérifiez les journaux :
   `[MyIntranet OS] v1.0.0 initialisé : 1 application(s), serveur web sur le port 8090 (...)`.
5. Pour que la tablette s'ouvre directement avec F2, mettez son adresse en page d'accueil du navigateur :
   champ `tabletUrl` de la configuration du serveur Nova-Life, ou option `SetAsBrowserHomePage: true` ci-dessous.

## Configuration

| Option | Défaut | Description |
|---|---|---|
| `Port` | `8090` | Port TCP du serveur web de la tablette. |
| `BindAddress` | `0.0.0.0` | Interface d'écoute (`0.0.0.0` = toutes). |
| `PublicUrl` | `""` | Adresse affichée aux joueurs (`/tablette`) et utilisée pour la page d'accueil. |
| `TabletName` | `MyIntranet OS` | Nom affiché. |
| `ChatCommand` / `ChatCommandAliases` | `/tablette` / `["/intranet"]` | Commande de chat. |
| `AutoLoginByIp` | `true` | Identification automatique si un seul joueur connecté a la même IP que le navigateur. |
| `PairingCodeEnabled` | `true` | Code d'association à 6 chiffres si l'identification automatique échoue (même IP partagée…). |
| `PairingCodeLifetimeSeconds` | `300` | Validité d'un code d'association. |
| `SessionLifetimeHours` | `12` | Durée maximale d'une session (elle se ferme aussi à la déconnexion du joueur). |
| `PinMaxAttempts` | `5` | Essais avant blocage. |
| `PinLockoutSeconds` | `300` | Premier blocage ; doublé à chaque récidive (1 h max). |
| `TrustProxyHeaders` | `false` | `true` uniquement derrière un reverse proxy (utilise `X-Forwarded-For`). |
| `SetAsBrowserHomePage` | `false` | Définit `PublicUrl` comme page d'accueil du navigateur en jeu. |
| `ShowMoney` | `true` | Expose l'argent et la banque aux applications. |

## Commandes

| Commande | Effet |
|---|---|
| `/tablette` | Affiche l'adresse de la tablette. |
| `/tablette <code>` | Associe le navigateur affichant ce code à votre personnage. |
| `/tablette resetcode` | Supprime votre code de verrouillage (code oublié). |
| `/tablette deconnexion` | Ferme toutes vos sessions de tablette. |
| `/tablette reload` | (staff) Recharge les applications. |

## Créer des applications

Voir [DOCUMENTATION.md](DOCUMENTATION.md) (aussi copiée dans le dossier `MyIntranetOS/` du serveur).

## Sécurité

- L'API ne donne accès qu'au personnage de la session ; elle est **en lecture seule** sur les données de jeu.
- Les écritures exigent l'en-tête `X-MyIntranet: 1` (bloque les requêtes venant d'autres sites).
- L'identification par IP suppose que deux joueurs différents n'utilisent pas la même connexion ; dans ce cas
  le code d'association est demandé automatiquement. Le code de verrouillage protège la tablette sur un PC partagé.
- Le site est servi en HTTP : pour du HTTPS, placez-le derrière un reverse proxy (nginx, Caddy) et activez
  `TrustProxyHeaders`.

## Compilation

1. Copiez les DLL du jeu dans `libs/` (voir [libs/README.md](libs/README.md)).
2. `dotnet build -c Release` → `bin/Release/net472/MyIntranetOS.dll`.

L'interface (`Web/`), l'application Réglages (`Applications/reglages/`) et la documentation sont embarquées dans
la DLL : un seul fichier à installer.
