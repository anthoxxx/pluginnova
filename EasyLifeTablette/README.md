# Tablette Easy Life

Une tablette façon iPad pour le serveur **Easy Life** (Nova-Life: Amboise), utilisable :
- **en jeu**, sur l'ordinateur (F2), avec un code donné par la commande `/tablette` ;
- **hors du jeu**, dans n'importe quel navigateur (PC, téléphone), avec **« Se connecter avec Steam »**.

Le projet a deux parties :

| Dossier | Rôle |
|---|---|
| `Site/` | Le site web de la tablette (Node.js + SQLite) : c'est la tablette elle-même. |
| `Plugin/` | Le plugin du serveur Nova-Life : il envoie au site l'argent, la banque et le statut staff des joueurs, affiche en jeu les notifications de la tablette et donne le code de connexion. |

## Applications

| Application | Ce qu'elle fait |
|---|---|
| **Accueil** | Widgets date/heure, **Mon argent** (solde synchronisé depuis le jeu), notifications, actualités, recherche, dock. |
| **Mail** | Adresses `@easylife.fr` : création, plusieurs adresses par joueur, adresses partagées (une entreprise donne le mot de passe à ses employés), boîte de réception, envoyés, réponse, suppression. |
| **Annuaire** | Les entreprises du serveur (ajoutées automatiquement) + fiches ajoutées par le staff : numéro, catégorie, description, badge « Recrute ». Un gérant peut être désigné pour modifier sa fiche. |
| **Actualités** | Articles du staff et des journalistes, rubriques, article épinglé « À la une ». |
| **Le Bon Plan** | Petites annonces : 10 catégories, photo (lien), prix ou « Prix sur demande », favoris, mes annonces, messagerie acheteur/vendeur, vues, expiration après 30 jours, clôture, signalement au staff. |
| **Commander** | Restaurants type **McBlue** : carte (menus, burgers, boissons… avec « À la une »), panier, commande au comptoir ou en livraison, suivi « Mes commandes ». Espace équipe : prise de service (le restaurant est **ouvert** seulement si quelqu'un est en service), commandes en direct, ruptures de stock, gestion de la carte et de l'équipe par le gérant. |
| **Banque** | Solde et liquide synchronisés depuis le jeu, historique de l'évolution du solde. |
| **Notes** | Bloc-notes personnel avec enregistrement automatique. |
| **Réglages** | Compte Steam (pseudo, SteamID), verrouillage de la tablette par **code**, fond d'écran, couleur, taille du texte, applications affichées, notifications en jeu, appareils connectés, déconnexion. |
| **Administration** (staff) | Statistiques, journalistes, signalements du Bon Plan. |

Toutes les notifications (nouveau mail, message du Bon Plan, commande reçue ou prête…) apparaissent sur
la tablette **et à l'écran en jeu** si le joueur est en ville.

---

## 1. Installer le site

Il faut une machine allumée en permanence (le serveur de jeu ou un VPS) avec **Node.js 22.13 ou plus récent**.

```bash
cd Site
npm install
cp config.example.json config.json
```

Remplissez `config.json` :

| Clé | Valeur |
|---|---|
| `serverName` | `Easy Life` |
| `baseUrl` | L'adresse **publique** exacte du site, sans `/` final, par ex. `https://tablette.easylife.fr`. Obligatoire pour la connexion Steam. |
| `port` | Port local du site (3000 par défaut). |
| `gameApiKey` | Un mot de passe long et secret (32 caractères conseillés), à recopier dans le plugin. |
| `steamApiKey` | Facultatif : clé sur <https://steamcommunity.com/dev/apikey> pour afficher le pseudo et l'avatar Steam. |
| `adminSteamIds` | Les SteamID64 des fondateurs (toujours staff sur la tablette). Les admins du jeu deviennent staff automatiquement. |
| `mailDomain` | `easylife.fr` (domaine des adresses mail). |

Lancer :

```bash
npm start
```

Pour le laisser tourner en permanence : `npx pm2 start npm --name tablette -- start` (puis `npx pm2 save`).

**HTTPS** (recommandé) : mettez le site derrière un reverse proxy. Exemple avec Caddy (certificat automatique) :

```
tablette.easylife.fr {
    reverse_proxy 127.0.0.1:3000
}
```

La base de données est le fichier `Site/data/easylife.sqlite` : sauvegardez-le régulièrement.

## 2. Installer le plugin

1. Copier les 5 DLL listées dans `Plugin/libs/README.md` dans `Plugin/libs/` (ModKit et AAMenu ne sont **pas** nécessaires).
2. Ouvrir `Plugin/EasyLifeTablette.csproj` dans Visual Studio et générer en **Release** (ou `dotnet build -c Release`).
3. Copier `Plugin/bin/Release/net472/EasyLifeTablette.dll` dans le dossier `Plugins` du serveur.
4. Démarrer le serveur une fois : le fichier `Plugins/EasyLifeTablette/config.json` est créé. Le remplir :

| Clé | Valeur |
|---|---|
| `ApiUrl` | Adresse du site **vue depuis le serveur de jeu**. Même machine : `http://127.0.0.1:3000`. Sinon l'adresse publique. |
| `ApiKey` | La même valeur que `gameApiKey` du site. |
| `SyncSeconds` | Fréquence de synchronisation (30 s par défaut). |
| `SyncBusinesses` | `true` pour remplir l'Annuaire avec les entreprises du serveur. |
| `CodeKey` | Touche qui affiche aussi le code (ex. `"F2"`). `"None"` = seulement la commande `/tablette`. |

5. Redémarrer le serveur.

## 3. Utilisation

**Joueurs, en jeu** : taper `/tablette` (ou `/tab`) → un code s'affiche → ouvrir l'ordinateur (F2) → aller sur
l'adresse du site → saisir le code.

**Joueurs, hors du jeu** : aller sur l'adresse du site → **Se connecter avec Steam**.

**Staff** (premiers pas) :
- **Commander → Nouveau restaurant** : créer « McBlue » et choisir son gérant. Le gérant compose la carte et ajoute ses employés
  dans l'espace équipe ; les clients peuvent commander dès qu'un équipier a cliqué sur **Prendre le service**.
- **Administration → Journalistes** : autoriser des joueurs à publier dans Actualités.
- **Annuaire → Ajouter** : fiches hors entreprises du jeu (mairie, police, hôpital…).

## Sécurité

- Le plugin s'authentifie avec la clé `gameApiKey` : ne la partagez pas.
- La connexion Steam est vérifiée auprès de Steam (OpenID) ; les codes de jeu sont à usage unique et expirent en 10 minutes.
- Le code de la tablette (Réglages → Code & sécurité) est demandé à chaque nouvelle connexion et au déverrouillage.
- Les textes écrits par les joueurs sont toujours affichés comme du texte (pas de HTML), les photos d'annonces doivent être en `https://`.

## À vérifier sur votre serveur

La documentation officielle de l'API Nova-Life n'était pas accessible pendant le développement. Le plugin
n'utilise que des éléments de l'API déjà présents dans ce dépôt, plus quelques autres à confirmer :
`player.steamId`, `player.character.Money` / `player.character.Bank`.
Si la compilation signale l'un d'eux, il suffit de corriger le nom dans `Plugin/EasyLifeTablette.cs` (méthode `Sync`).
Les entreprises sont lues par réflexion sur `Nova.biz` : si rien n'est trouvé, un avertissement apparaît dans les
logs et l'Annuaire garde uniquement les fiches ajoutées par le staff.
