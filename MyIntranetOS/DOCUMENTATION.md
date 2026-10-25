# MyIntranet OS — Créer une application

MyIntranet OS n'a qu'une application installée : **Réglages**. Tout le reste, c'est vous qui le créez.
Une application est une simple page web (HTML/CSS/JS) affichée dans la tablette.

---

## 1. Structure

Déposez un dossier dans `MyIntranetOS/Applications/` :

```
MyIntranetOS/
└── Applications/
    ├── reglages/          ← application intégrée (recréée à chaque démarrage, ne pas modifier)
    └── notes/             ← votre application
        ├── app.json       ← manifeste (obligatoire)
        ├── index.html     ← page d'entrée (obligatoire)
        ├── icon.png       ← icône (facultative)
        └── ...            ← tous vos fichiers (js, css, images, sons…)
```

L'application apparaît sur l'écran d'accueil **en moins de 10 secondes, sans redémarrage**
(ou immédiatement avec `/tablette reload` pour le staff).

## 2. Le manifeste `app.json`

```json
{
  "id": "notes",
  "name": "Notes",
  "description": "Mes notes personnelles",
  "version": "1.0.0",
  "author": "Moi",
  "icon": "icon.png",
  "color": "#ff9f0a",
  "entry": "index.html",
  "adminOnly": false
}
```

| Champ | Obligatoire | Description |
|---|---|---|
| `id` | non (nom du dossier par défaut) | Identifiant unique : 2 à 32 caractères `a-z 0-9 - _`, commence par une lettre ou un chiffre. |
| `name` | conseillé | Nom affiché sous l'icône. |
| `description` | non | Affichée dans Réglages > Écran d'accueil. |
| `version`, `author` | non | Informatifs. |
| `icon` | non | Fichier image du dossier (`png`, `jpg`, `gif`, `svg`, `webp`) **ou** un emoji / texte court (`"📝"`). Par défaut : première lettre du nom. |
| `color` | non | Couleur de fond de l'icône, format `#rrggbb`. |
| `entry` | non | Page d'entrée, `index.html` par défaut. |
| `adminOnly` | non | `true` : visible uniquement par les joueurs staff. |
| `order` | non | Position par défaut sur l'écran d'accueil (plus petit = plus haut, 1000 par défaut). |

Les fichiers de l'application sont servis sur `/apps/<id>/...`.
Utilisez des **chemins relatifs** (`app.js`, `img/logo.png`) dans votre HTML.

## 3. Application minimale

`index.html` :

```html
<!doctype html>
<html lang="fr">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <link rel="stylesheet" href="/sdk/myintranet.css">
</head>
<body>
  <div class="mi-page">
    <h1>Bonjour <span id="name">…</span></h1>
    <div class="mi-list">
      <div class="mi-row"><span class="mi-grow">Banque</span><span id="bank" class="mi-muted"></span></div>
    </div>
    <p><button class="mi-btn" id="hello">Notifier</button></p>
  </div>

  <script src="/sdk/myintranet.js"></script>
  <script>
    MyIntranet.me().then(function (me) {
      document.getElementById('name').textContent = me.firstName;
      document.getElementById('bank').textContent = me.bank + ' €';
    });
    document.getElementById('hello').onclick = function () {
      MyIntranet.notify('Mon app', 'Ça marche !', 'success');
    };
  </script>
</body>
</html>
```

- `/sdk/myintranet.css` donne le style de la tablette (thème clair/sombre, couleur d'accent, taille du texte
  du joueur appliqués automatiquement).
- `/sdk/myintranet.js` donne l'objet global `MyIntranet`.

## 4. Le SDK `MyIntranet`

Toutes les fonctions d'API renvoient une **Promise**. En cas d'erreur, la promesse est rejetée avec une
`Error` qui possède `status` (code HTTP), `code` (code d'erreur) et `message`.

### Données

| Fonction | Résultat |
|---|---|
| `MyIntranet.me()` | Le personnage du joueur (voir `/api/me`). |
| `MyIntranet.apps()` | Les applications visibles par le joueur. |
| `MyIntranet.settings.get()` | Les préférences du joueur. |
| `MyIntranet.settings.update(patch)` | Modifie des préférences (appliquées immédiatement par la tablette). |
| `MyIntranet.storage.get(clé)` | Valeur enregistrée par **votre** application pour ce joueur, ou `null`. |
| `MyIntranet.storage.set(clé, valeur)` | Enregistre n'importe quelle valeur JSON (64 Ko max, 200 clés max par app). |
| `MyIntranet.storage.remove(clé)` | Supprime une clé. |
| `MyIntranet.storage.keys()` | Liste des clés `[{ key, updatedAt }]`. |
| `MyIntranet.api(chemin, { method, body })` | Appel libre à l'API (`chemin` sans `/api`). |

Le stockage est propre à chaque **personnage** et à chaque **application** et est sauvegardé dans la base
SQLite du plugin. Clés : 1 à 64 caractères `A-Z a-z 0-9 _ . -`.

```js
MyIntranet.storage.get('notes').then(function (notes) {
  notes = notes || [];
  notes.push({ text: 'Acheter du pain', date: Date.now() });
  return MyIntranet.storage.set('notes', notes);
});
```

### Communication avec la tablette

| Fonction | Effet |
|---|---|
| `MyIntranet.notify(titre, message, type)` | Notification de la tablette. `type` : `info`, `success`, `warning`, `error`. |
| `MyIntranet.close()` | Ferme l'application (retour à l'accueil). |
| `MyIntranet.openApp(id, params)` | Ouvre une autre application, avec des paramètres facultatifs (ex. `MyIntranet.openApp('messages', { number: '0612345678' })`). |
| `MyIntranet.params()` | Paramètres reçus à l'ouverture. |
| `MyIntranet.refresh()` | Demande à la tablette de rafraîchir argent, notifications et actualités. |
| `MyIntranet.setBadge(n)` | Pastille rouge sur l'icône (0 pour l'enlever). |
| `MyIntranet.lock()` | Verrouille la tablette. |
| `MyIntranet.ready(cb)` | `cb(context)` quand la tablette a répondu. `context = { app, settings, me, os }`. |
| `MyIntranet.on('settings', cb)` | Appelé quand le joueur change ses préférences (thème, couleur…). |
| `MyIntranet.inTablet` | `true` si la page est ouverte dans la tablette. |

### Utilitaires

`MyIntranet.applyAppearance(settings)`, `MyIntranet.resolveTheme(theme)`, `MyIntranet.wallpaperCss(id)`,
`MyIntranet.wallpapers`, `MyIntranet.accents`, `MyIntranet.version`.

### Kit d'interface `/sdk/ui.js` (facultatif)

Les applications intégrées utilisent `UI` : `UI.h(tag, attributs, ...enfants)`, `UI.header`, `UI.tabs`, `UI.sheet`
(fenêtre modale), `UI.confirm`, `UI.field`, `UI.avatar`, `UI.empty`, `UI.loading`, `UI.action` (bouton qui se
désactive pendant l'action), `UI.money`, `UI.ago`, `UI.date`, `UI.time`, `UI.success`, `UI.error`.
Lisez le code des applications de `Applications/` pour des exemples complets.

### Application de service sans code

Pour un nouveau service (ex. « Coiffeur »), ajoutez une entrée dans `Services` de `configuration.json`, puis créez
`Applications/coiffeur/` avec un `app.json` et cet `index.html` :

```html
<link rel="stylesheet" href="/sdk/myintranet.css">
<div class="mi-page" id="app" data-service="coiffeur"></div>
<script src="/sdk/myintranet.js"></script>
<script src="/sdk/ui.js"></script>
<script src="/sdk/service-app.js"></script>
```

## 5. Protocole `postMessage` (sans le SDK)

Le SDK n'est qu'une surcouche. Vous pouvez dialoguer directement avec la tablette :

```js
// application -> tablette
window.parent.postMessage({ myintranet: 1, type: 'notify', payload: { title: 'Hey', message: '…', type: 'info' } }, location.origin);

// requête avec réponse : ajoutez un id
window.parent.postMessage({ myintranet: 1, type: 'context', id: 1 }, location.origin);

// tablette -> application
window.addEventListener('message', function (e) {
  if (e.origin !== location.origin || !e.data || e.data.myintranet !== 1) return;
  // e.data.type === 'response' (avec e.data.id), 'settings' (payload = préférences)
});
```

| `type` envoyé par l'application | `payload` | Réponse |
|---|---|---|
| `context` | — | `{ app, settings, me, os }` |
| `notify` | `{ title, message, type }` | — |
| `close` | — | — |
| `open-app` | `{ id }` | — |
| `badge` | `{ count }` | — |
| `lock` | — | — |
| `settings-updated` | préférences complètes (après `POST /api/settings`) | — |
| `activity` | — | (repousse le verrouillage automatique) |

Les messages doivent venir de la même origine que la tablette ; les autres sont ignorés.

## 6. API HTTP

Toutes les routes répondent en JSON. Le joueur est identifié par un cookie de session (envoyé automatiquement
par le navigateur). Les requêtes `POST`, `PUT` et `DELETE` doivent contenir l'en-tête **`X-MyIntranet: 1`**
(protection contre les requêtes venant d'autres sites) et un corps JSON (`Content-Type: application/json`).

| Méthode | Route | Description |
|---|---|---|
| GET | `/api/info` | Nom de la tablette, version, nom du serveur (sans session). |
| GET | `/api/auth/session` | État de la session (`ok`, `pairing`, `unknown`). |
| POST | `/api/auth/logout` | Ferme la session. |
| POST | `/api/lock/unlock` | `{ "pin": "1234" }` déverrouille. |
| POST | `/api/lock/lock` | Verrouille. |
| POST | `/api/lock/pin` | `{ "current": "1234", "pin": "5678" }` change le code (`"pin": ""` le supprime). |
| GET | `/api/me` | Le personnage. |
| GET | `/api/apps` | Les applications. |
| GET / POST | `/api/settings` | Lire / modifier les préférences (modification partielle). |
| POST | `/api/settings/reset` | Préférences par défaut. |
| GET | `/api/storage/<appId>` | Clés enregistrées. |
| GET / POST / DELETE | `/api/storage/<appId>/<clé>` | Lire / écrire (`{ "value": ... }`) / supprimer. |

### Routes des applications intégrées (reliées au jeu)

Utilisables aussi par vos applications.

| Méthode | Route | Description |
|---|---|---|
| GET | `/api/notifications` | `{ unread, items: [{ id, app, title, message, type, read, createdAt }] }` |
| POST | `/api/notifications/read` | `{ "ids": [1, 2] }` (sans `ids` : tout marquer comme lu). |
| POST | `/api/notifications/clear` | Efface toutes les notifications. |
| GET | `/api/wallet` | Identité, permis, argent. |
| GET | `/api/bank` | Solde, historique. |
| POST | `/api/bank/transfer` | `{ "to": "0612345678" ou "Prénom Nom", "amount": 150, "label": "Loyer" }` |
| GET | `/api/sms` | Conversations (téléphone du jeu). |
| GET | `/api/sms/<numéro>` | Messages d'une conversation. |
| POST | `/api/sms` | `{ "to": "0612345678", "message": "…" }` |
| GET / POST | `/api/contacts` | Contacts du téléphone du jeu (`{ "name", "number" }`). |
| DELETE | `/api/contacts/<id>` | Supprime un contact. |
| GET | `/api/directory` | Entreprises (nom, description, contact, activités, recrutement). |
| GET | `/api/mail/accounts` | Boîtes accessibles (`mine`) et annuaire des adresses (`directory`). |
| GET | `/api/mail/<boîte>` | Mails d'une boîte. |
| POST | `/api/mail/send` | `{ "from": "<id boîte>", "to": "<domaine>", "subject", "content" }` |
| DELETE | `/api/mail/<boîte>/<guid>` | Supprime un mail. |
| GET / POST | `/api/news` | Articles (`canPublish` indique si le joueur peut publier `{ "title", "body" }`). |
| DELETE | `/api/news/<id>` | Supprime un article (auteur ou staff). |
| GET / POST | `/api/birdy` | Fil (`?before=<id>` pour paginer) / publier `{ "text" }`. |
| POST | `/api/birdy/<id>/like` | J'aime (bascule). |
| DELETE | `/api/birdy/<id>` | Supprime un message (auteur ou staff). |
| GET | `/api/vehicles` | Véhicules du personnage. |
| POST | `/api/vehicles/<id>/locate` | Place un point GPS en jeu sur le véhicule. |
| GET | `/api/realestate` | `{ mine, forSale }` |
| POST | `/api/realestate/<id>/locate` | Point GPS en jeu sur le terrain. |
| GET / POST | `/api/ads` | Petites annonces / publier `{ "title", "category", "price", "description" }`. |
| DELETE | `/api/ads/<id>` | Retire une annonce. |
| GET | `/api/services` | Services configurés. |
| GET | `/api/services/<id>` | Détail : `provider`, `available`, `businesses`, `mine`, `queue`. |
| POST | `/api/services/<id>/requests` | `{ "message", "sharePosition": true }` notifie les joueurs en service. |
| POST | `/api/services/requests/<id>/<action>` | `accept`, `locate`, `done`, `cancel`. |
| GET | `/api/radio` | Stations configurées. |

Les erreurs du jeu (serveur occupé) renvoient `503`. Les actions répétées trop vite renvoient `429`.

Codes d'erreur : `401` session expirée (la tablette se réidentifie), `423` tablette verrouillée,
`429` trop de tentatives, `400` donnée invalide, `404` introuvable.
Format : `{ "error": "code", "message": "texte lisible" }`.

### `GET /api/me`

```json
{
  "steamId": "76561198000000000",
  "steamName": "Pseudo",
  "characterId": 12,
  "firstName": "Jean",
  "lastName": "Dupont",
  "fullName": "Jean Dupont",
  "phoneNumber": "0612345678",
  "birthday": "01/01/1990",
  "level": 4,
  "xp": 350,
  "money": 120.5,
  "bank": 4200,
  "biz": { "id": 3, "name": "Garage Amboise" },
  "isAdmin": false,
  "updatedAt": 1767225600
}
```

`money` et `bank` valent `null` si l'option `ShowMoney` est désactivée. Les données sont rafraîchies à chaque
mouvement d'argent et chaque minute de jeu.

### `GET /api/apps`

```json
[
  {
    "id": "notes", "name": "Notes", "description": "…", "version": "1.0.0", "author": "Moi",
    "color": "#ff9f0a", "icon": { "type": "image", "value": "/apps/notes/icon.png" },
    "url": "/apps/notes/index.html", "builtin": false, "adminOnly": false, "hidden": false
  }
]
```

Triées dans l'ordre choisi par le joueur. `hidden` : masquée de l'écran d'accueil.

### `GET /api/settings`

```json
{
  "theme": "auto", "accent": "#0a84ff", "wallpaper": "aurora",
  "brightness": 100, "textScale": 100, "use24h": true, "autoLockMinutes": 5,
  "appOrder": ["reglages", "notes"], "hiddenApps": [], "dock": ["reglages"],
  "widgets": ["clock", "profile", "wallet"], "hasPin": false
}
```

| Préférence | Valeurs |
|---|---|
| `theme` | `auto`, `light`, `dark` |
| `accent` | `#rrggbb` |
| `wallpaper` | `aurora`, `ocean`, `sunset`, `forest`, `night`, `nova`, `graphite`, `candy` ou `url:https://…` |
| `brightness` | 20 à 100 |
| `textScale` | 80 à 140 (%) |
| `use24h` | booléen |
| `autoLockMinutes` | 0 (jamais) à 60 |
| `appOrder`, `hiddenApps` | listes d'identifiants d'applications |
| `dock` | 4 identifiants maximum |
| `widgets` | parmi `clock`, `profile`, `wallet`, `calendar` |

## 7. Bonnes pratiques

- Testez votre application dans la tablette (pas directement à l'adresse `/apps/<id>/`) pour bénéficier du thème
  et du contexte.
- Utilisez les variables CSS du SDK (`var(--mi-accent)`, `var(--mi-surface)`, `var(--mi-text)`…) pour suivre le
  thème du joueur.
- Ne faites jamais confiance aux données envoyées par le navigateur pour des actions de jeu : l'API est en
  lecture seule sur le personnage.
- Pas d'accès Internet requis : embarquez vos bibliothèques dans le dossier de l'application.
