# France Travail

Plugin Nova-Life : les joueurs **s'actualisent** auprès de France Travail en répondant à un
questionnaire, et les entreprises « France Travail » reçoivent ces actualisations **en temps
réel** dans leur **espace patron**, où elles peuvent les traiter et **proposer des emplois** aux
demandeurs.

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) doivent être installés sur le serveur.

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans le dossier `FranceTravail/libs/`.
2. Ouvrir **`FranceTravail.csproj`** directement (Fichier → Ouvrir → Projet/Solution).
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/FranceTravail.dll` dans le dossier `Plugins` du serveur.

## Installation (staff)
Au premier démarrage, le plugin crée l'activité personnalisée AAMenu **« France Travail »**.
Il faut ensuite déclarer quelle(s) entreprise(s) sont des agences France Travail :
- AAMenu → **Administration → Activités personnalisées → France Travail → Ajouter une société**, ou
- commande **`/ftagence <id entreprise>`** (staff en service admin ; sans id = votre propre entreprise).

## Côté joueur
AAMenu → **Interactions → France Travail** (ou `/francetravail`, `/ft`) :
- **M'actualiser** : questionnaire à choix
  1. Avez-vous travaillé cette semaine ? → si oui : combien d'heures, pour quelle entreprise
  2. Avez-vous été en arrêt maladie ?
  3. Avez-vous suivi une formation ?
  4. Recherchez-vous un emploi ? (oui / j'ai trouvé un emploi / je ne recherche plus)
  5. Si oui : quel secteur recherchez-vous ? et êtes-vous disponible immédiatement ?
  6. Commentaire pour le conseiller (facultatif)
  7. Récapitulatif → **Envoyer**

  Une seule actualisation par période (24 h réelles par défaut, voir la configuration).
- **Mes actualisations** : historique et statut (en attente / validée / refusée).
- **Mes propositions d'emploi** : offres envoyées par France Travail → **Accepter** ou **Refuser**.

Le joueur reçoit une notification quand son actualisation est traitée ou qu'une offre lui est faite.

## Côté entreprise (espace patron)
AAMenu → **Métier → France Travail - Espace patron** (ou `/ftpatron`).
Réservé au patron et aux employés qui peuvent gérer les employés (ou à tous les employés,
voir la configuration).
- **Actualisations à traiter** : arrivent en direct, avec le détail des réponses → **Valider** / **Refuser**.
- **Demandeurs d'emploi** : dernière situation de chaque joueur qui recherche un emploi (secteur,
  disponibilité) → dossier → **Proposer un emploi** (poste, entreprise qui recrute, détails).
- **Propositions envoyées** : suivi des réponses des joueurs (acceptée / refusée).
- **Historique des actualisations** : les 100 dernières.

**Temps réel** : quand un joueur s'actualise ou répond à une offre, tous les membres connectés des
agences reçoivent une notification, et les listes ouvertes de l'espace patron se mettent à jour
automatiquement.

## Configuration
`Plugins/FranceTravail/config.json` (créé au premier démarrage) :

| Clé | Défaut | Rôle |
|---|---|---|
| `DelaiEntreActualisationsHeures` | `24` | Délai minimum entre deux actualisations d'un joueur |
| `AccesTousLesEmployes` | `false` | `true` : tous les employés de l'agence ont l'espace patron |
| `Secteurs` | liste | Secteurs proposés dans la question « Quel secteur recherchez-vous ? » |

Les données sont sauvegardées dans la base ModKit (`Plugins/ModKit/data.sqlite`).
