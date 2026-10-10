# TaxiJob

**By Matheo Mercier**

Job taxi complet pour Nova-Life, qui remplace `jobtaxibymatheo.dll`.
Toutes les courses se font **en voiture** : les points sont orange (points véhicule) et ne
se déclenchent qu'au volant. L'ancien plugin utilisait des points bleus, qu'il fallait
prendre à pied.

Le passage sur un point est détecté de deux façons : par le signal du jeu, et par le serveur
lui-même, qui compare 4 fois par seconde la position de la voiture au point (rayon réglable,
8 m par défaut). Le jeu n'envoie son signal qu'une fois, à l'entrée de la voiture dans le
point, et le refuse si le personnage, en retard sur le réseau, est à plus de 10 m. En roulant
un peu vite, le point ne se validait donc jamais. La détection côté serveur corrige ce cas.

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) doivent être installés sur le serveur.
- **Supprimer `jobtaxibymatheo.dll`** du dossier `Plugins`. Au premier lancement, son
  `TaxiJob/config.json` (entreprise, paie min/max, points) est importé automatiquement,
  puis renommé en `config.json.importe`.

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans `TaxiJob/libs/`.
2. Ouvrir **`TaxiJob.csproj`** puis générer en **Release** (ou `dotnet build -c Release`).
3. Copier `bin/Release/net472/TaxiJob.dll` dans le dossier `Plugins` du serveur.

## Chauffeur
- **AAMenu → Métier** (entreprise d'activité taxi) : menu chauffeur, prise de service, course.
- **`/taxi`** : menu chauffeur ; `/taxi service`, `/taxi course`, `/taxi annuler`.
- **Centrales taxi** : points bleus placés par le staff qui ouvrent le menu chauffeur.

### Courses en solo (clients PNJ, comme un farm)
1. Au volant, lancer une course : un client attend sur un point de course (point orange).
2. Arrivé au point, le client monte et une destination est tirée au hasard.
3. Le tarif est calculé selon la distance : prise en charge + prix au km, avec un minimum
   et un maximum. Le chauffeur reçoit un **pourboire** s'il arrive avant le temps indiqué.
4. Le chauffeur est payé à l'arrivée. Si l'option est activée, une nouvelle course
   s'enchaîne automatiquement.

### Appels de vrais joueurs
- Un joueur tape **`/appeltaxi [message]`** (ou `/appeltaxi annuler`).
- Les chauffeurs en service sont prévenus et l'acceptent depuis `/taxi → Appels clients`.
- Un point orange guide le chauffeur jusqu'au client, qui est prévenu à chaque étape.
  Le prix se négocie en RP, et une prime de prise en charge est possible en option.

## Staff : tout se configure en jeu
Être admin, puis :
- **AAMenu → Administration → Plugins → Taxi - Configuration du job**, ou
- **AAMenu → Administration → Points bleus → Taxi**, ou
- **`/taxiadmin`** (en service admin).

| Menu | Contenu |
|---|---|
| Paramètres du job | ID de l'entreprise (**0 = ouvert à tous**, comme un farm), prise de service obligatoire, être au volant pour lancer une course, véhicule de l'entreprise taxi obligatoire, rayon de validation d'un point, prise en charge, prix au km, paie min/max, pourboire (%), vitesse de référence, distance minimum, enchaînement automatique, courses max par heure, appels joueurs, expiration des appels, prime d'appel |
| Points de course | **Ajouter ici** (à votre position, sur la route), renommer, type (prise en charge et/ou dépose), activer/désactiver, déplacer ici, se téléporter, supprimer |
| Véhicules autorisés | Liste vide = tous les véhicules. **Ajouter mon véhicule** (monter dedans) / retirer |
| Centrales taxi | Placer une centrale ici, liste des points (TP, déplacer, supprimer), modèles |
| Courses en cours | Voir et annuler la course d'un chauffeur |
| Appels clients | Voir et supprimer les appels |
| Classement | Top des chauffeurs et remise à zéro des statistiques |

Les données sont stockées dans la base ModKit (`Plugins/ModKit/data.sqlite`) :
tables `TaxiSettings`, `TaxiPoint`, `TaxiDriverStats` et `TaxiStation`.

Il faut au moins **2 points de course** (un de prise en charge et un de dépose) pour
pouvoir faire des courses PNJ.
