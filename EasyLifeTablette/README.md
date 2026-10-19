# Tablette Easy Life

## Tablette en jeu (plugin seul, aucun site nécessaire)

Mettez `EasyLifeTablette.dll` dans le dossier `Plugins` du serveur et redémarrez. En jeu : **`/tablette`** (ou `/tab`).
Pour l'ouvrir aussi avec une touche : `Plugins/EasyLifeTablette/config.json` → `"Key": "F3"` (par exemple).

| Application | Ce qu'elle fait |
|---|---|
| **Accueil** | Date et heure, solde bancaire, mails non lus, dernière actualité, notifications. |
| **Mail** | Une adresse par joueur (`prenom.nom@easylife.fr`), boîte de réception, envoyés, écrire (recherche par nom, adresse ou numéro), répondre, supprimer. |
| **Annuaire** | Toutes les entreprises du serveur (contact, recrutement, description) + fiches ajoutées par le staff. « Écrire au gérant ». |
| **Actualités** | Articles du staff et des journalistes, rubriques, « À la une ». |
| **Le Bon Plan** | Petites annonces : 10 catégories, prix ou « Prix sur demande », favoris, mes annonces, contact du vendeur par mail, clôture, expiration après 30 jours. |
| **Commander** | Les entreprises qui ont une carte deviennent des restaurants. Un employé clique sur **Prendre le service** (sinon le restaurant est fermé). Le client paie avec son compte bancaire ; à « Terminée », l'argent part sur le compte de l'entreprise ; à « Annulée », le client est remboursé. Le patron gère la carte depuis **Espace équipe**. |
| **Banque** | Solde et liquide, **virements** vers les joueurs en ville, historique. |
| **Notes**, **Notifications**, **Réglages** (code de verrouillage, notifications à l'écran) et **Administration** (journalistes, fiches d'annuaire) pour le staff. |

Les données sont dans `Plugins/EasyLifeTablette/data.json`. Options de `config.json` : `ServerName`, `MailDomain`,
`Key`, `MaxTransfer` (plafond par virement), `DeliveryFee` (frais de livraison), `MaxAdsPerPlayer`.

Compilation : mettre les 5 DLL de `Plugin/libs/README.md` dans `Plugin/libs/`, ouvrir `Plugin/EasyLifeTablette.csproj`,
générer en Release.

## Site web (pour plus tard)

Le dossier `Site/` contient une version web de la tablette (connexion Steam, accessible hors du jeu). Il n'est pas
encore relié au plugin en jeu.

### Détails du site

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

