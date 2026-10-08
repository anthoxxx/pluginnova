# Drugs Dealers By Loris Strange

Plugin Nova-Life : les joueurs revendent à des **clients (PNJ acheteurs)** les marchandises
choisies par le staff, seulement pendant les **heures d'ouverture**, avec des **prix qui
changent toutes les 15 minutes** et des **alertes envoyées aux forces de l'ordre**.

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) doivent être installés sur le serveur.

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans le dossier `DrugsDealers/libs/`.
2. Ouvrir **`DrugsDealers.csproj`** (Fichier → Ouvrir → Projet/Solution).
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/DrugsDealers.dll` dans le dossier `Plugins` du serveur.

## Joueur : vendre
1. Menu AAMenu → **Interactions → Vendre ma marchandise** (ou commande `/dealer`).
2. Notification : *« Viens me vendre ta marchandise à ce point là ! »*. Un point est mis sur le
   **GPS** et un point bleu apparaît à l'endroit du rendez-vous.
3. Une fois sur le point, le menu de vente s'ouvre : choisir la marchandise, la quantité (ou
   **Tout vendre**). L'argent est donné en liquide.
4. Chaque client veut une quantité limitée. Quand on lui a **tout vendu, il disparaît** et ne
   revient sur ce lieu qu'après le délai de réapparition. On peut aussi le quitter (**Partir**).

Le client part si le joueur met trop de temps à venir, ou quand l'heure de fermeture arrive.
Si le menu a été fermé, refaire « Vendre ma marchandise » à côté du client pour le rouvrir.

## Staff : configuration
Être admin **et en service admin**, puis AAMenu → **Administration → Plugins → Drugs Dealers**
(ou `/drugsdealers`, alias `/dd`). Tout est enregistré dans la base ModKit.

| Réglage | Défaut | Rôle |
|---|---|---|
| Marchandises | — | Items achetés par les clients, avec un **prix min et max**. Ajout depuis son inventaire ou par ID. |
| Lieux de vente | — | **Créer ici** à sa position. Chaque lieu a son **% de prix** (ex. 80 % = moins cher, 150 % = plus cher) et la **quantité min/max** que veut son client. Se téléporter, déplacer, supprimer. |
| Prix actuels | — | Prix du marché en cours + bouton pour retirer de nouveaux prix. |
| Heures d'ouverture / fermeture | 22h → 5h | Heure du serveur. Passe minuit si besoin. Même heure = toujours ouvert. |
| Changement des prix | 15 min | Un nouveau prix est tiré au hasard entre min et max pour chaque marchandise. |
| Temps pour rejoindre le client | 10 min | Après ça le client s'en va. |
| Réapparition d'un client | 20 min | Délai avant qu'un nouveau client revienne sur un lieu vidé. |
| Délai entre deux clients | 60 s | Par joueur. |
| Alerte police | 30 % | Chance, à la première vente avec un client, que la police reçoive une notification. |
| Imprécision de l'alerte | 50 m | Le point GPS envoyé à la police est décalé au hasard. |
| Point GPS police | oui | Mettre le lieu approximatif sur le GPS des policiers. |
| Prévenir le vendeur | non | Le vendeur sait qu'il a été repéré. |
| Policiers en service requis | 0 | Nombre minimum de policiers en service pour pouvoir vendre. |
| La police en service peut vendre | non | |

Le prix payé = prix du marché actuel × % du lieu. Les policiers alertés sont les joueurs
**en service** d'une entreprise ayant l'activité **forces de l'ordre** (LawEnforcement).

## À propos des PNJ
L'API de Nova-Life ne permet pas de faire apparaître un personnage (PNJ) visible depuis un
plugin. Les clients sont donc représentés par le **point de rendez-vous** (point bleu + GPS) :
chacun a sa propre demande, son prix et disparaît quand on lui a tout vendu.
