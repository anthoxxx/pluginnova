# MyIntranet OS

Une vraie tablette intranet pour les joueurs de **Nova-Life: Amboise**, accessible depuis le navigateur du PC
en jeu (**F2**). Écran de verrouillage, widgets, dock, centre de contrôle et applications installables.

Chaque joueur a sa propre tablette, liée à son **compte Steam et à son personnage**. Les préférences sont
sauvegardées dans une base **SQLite**. Le plugin héberge son propre serveur web : **aucune dépendance externe**
(ni ModKit, ni AAMenu, ni serveur web à installer).

## Applications intégrées (reliées au jeu)

| Application | Ce qu'elle fait en jeu |
|---|---|
| **Portefeuille** | Argent en banque et en espèces, carte d'identité, permis et points, entreprise. |
| **Banque** | Solde réel, historique de tous les mouvements bancaires du jeu, **virements entre joueurs** (débit et crédit réels ; crédité à la connexion si le bénéficiaire est hors ligne). |
| **Messages** | Les **SMS du téléphone du jeu** : lecture et envoi, le téléphone du destinataire est mis à jour en direct. |
| **Annuaire** | Les **contacts du téléphone du jeu** (ajout, suppression) et toutes les entreprises de la ville. |
| **Mail** | Les **boîtes mail du jeu** (domaines d'entreprises et services) auxquelles le personnage a accès : lire, écrire, répondre, supprimer. |
| **Actualités** | Articles publiés par la presse / les services publics autorisés / le staff, affichés en widget. |
| **Birdy** | Réseau social de la ville : messages de 280 caractères, j'aime. |
| **Véhicules** | Véhicules du personnage (propriétaire ou co-propriétaire), carburant, **point GPS en jeu** sur le véhicule. |
| **Immobilier** | Terrains du personnage et biens à vendre / louer, avec **point GPS en jeu**. |
| **Le Bon Plan** | Petites annonces entre habitants (expiration automatique), contact par SMS. |
| **Taxi, Bus, Blue Eats, Santé, Assurances, Avocats, Grossiste, Agriculteurs, Service Public, Dépannage** | Demande envoyée **aux joueurs en service** de l'entreprise concernée (notification en jeu). Le prestataire accepte depuis sa tablette et reçoit un **point GPS** vers le demandeur. |
| **Jeux** | 2048 et Serpent (record par personnage). |
| **Radio** | Stations configurables. |
| **Réglages** | Apparence, écran d'accueil, code et sécurité. |

Les notifications (virement reçu, demande acceptée, nouveau mail…) s'affichent **en jeu** et dans la tablette
(pastille dans la barre d'état, widget, centre de notifications).

L'administrateur peut supprimer n'importe quelle application intégrée (sauf Réglages) : il suffit d'effacer son
dossier dans `Applications/`, elle ne sera pas réinstallée.

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
| `ProxySecret` | `""` | Clé partagée avec le proxy : l'adresse du joueur transmise n'est acceptée que si la requête porte `X-MyIntranet-Proxy` avec cette valeur. |
| `SetAsBrowserHomePage` | `false` | Définit `PublicUrl` comme page d'accueil du navigateur en jeu. |
| `ShowMoney` | `true` | Expose l'argent et la banque aux applications. |
| `BankTransfersEnabled` | `true` | Autorise les virements depuis l'application Banque. |
| `BankTransferMax` | `50000` | Montant maximum d'un virement. |
| `BankTransferCooldownSeconds` | `5` | Délai minimum entre deux virements d'un même joueur. |
| `NewsPublisherBizIds` | `[]` | Entreprises (identifiants) autorisées à publier des actualités. |
| `NewsPublisherActivities` | `["LawEnforcement"]` | Activités autorisées à publier (le staff peut toujours publier). |
| `NewsNotifyPlayers` | `true` | Notifie les joueurs connectés à chaque nouvel article. |
| `AdsLifetimeDays` / `AdsMaxPerPlayer` | `7` / `5` | Durée de vie et nombre maximum d'annonces Le Bon Plan. |
| `ServiceRequestLifetimeMinutes` | `30` | Une demande de service non traitée expire après ce délai. |
| `Services` | voir ci-dessous | Services joignables (Taxi, Santé…). |
| `RadioStations` | 4 stations | Liste `{ "Name", "Url" }` de flux audio. |

### Services

Chaque service relie une application aux joueurs d'entreprises précises :

```json
{ "Id": "taxi", "Name": "Taxi", "Activities": ["Taxi"], "BizIds": [], "RequireOnDuty": true,
  "RequestLabel": "Commander un taxi", "Placeholder": "Destination, nombre de passagers…" }
```

- `Activities` : activités d'entreprise du jeu : `LawEnforcement`, `Medical`, `Mecanic`, `Bus`, `Trash`, `Taxi`,
  `Transport`, `Hairdresser`, `Chef`, `Electrician`, `Fourriere`, `DIR`, `Agricole`.
- `BizIds` : identifiants d'entreprises (ex. le cabinet d'avocats, l'assureur, la mairie).
- `RequireOnDuty` : seuls les joueurs **en service** reçoivent les demandes.

Par défaut, **Assurances** et **Avocats** n'ont aucune activité correspondante dans le jeu : renseignez leurs
`BizIds`, sinon l'application affiche « service non configuré ».

## Adresse en https avec Netlify

Netlify peut servir de passerelle : `https://votre-site.netlify.app` relaie tout vers le plugin.
`netlify.toml` :

```toml
[build]
  publish = "."

[[redirects]]
  from = "/*"
  to = "http://IP-DU-SERVEUR:8090/:splat"
  status = 200
  force = true
  headers = {X-MyIntranet-Proxy = "une-longue-cle-secrete"}
```

Côté plugin : `"TrustProxyHeaders": true`, `"ProxySecret": "une-longue-cle-secrete"` et `PublicUrl` sur l'adresse
Netlify. Sans la clé, une requête directe ne peut pas se faire passer pour un joueur (le code d'association est demandé).

## Commandes

| Commande | Effet |
|---|---|
| `/tablette` | Affiche l'adresse de la tablette. |
| `/tablette <code>` | Associe le navigateur affichant ce code à votre personnage. |
| `/tablette resetcode` | Supprime votre code de verrouillage (code oublié). |
| `/tablette deconnexion` | Ferme toutes vos sessions de tablette. |
| `/tablette reload` | (staff) Recharge les applications. |

## Fonctionnement technique

Le serveur web tourne sur son propre thread. Toute action sur le jeu (argent, SMS, GPS, notifications, lecture des
véhicules et terrains) est exécutée sur le **thread principal du jeu** via une file vidée à chaque image, puis le
résultat est renvoyé à la tablette. Les SMS, contacts et mails utilisent les tables et méthodes du jeu
(`LifeDB`, `Nova.SendMail`), ils sont donc partagés avec le téléphone et le PC en jeu.

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
