namespace PermisArmePlugin;

public class PermisMessages
{
	public string PermisDejaPossede { get; set; } = "Vous possédez déjà un permis de port d'arme.";


	public string PasAssezArgent { get; set; } = "Il vous faut {prix}€ pour passer l'examen.";


	public string DelaiAttente { get; set; } = "Vous devez attendre encore {minutes} min.";


	public string Reussite { get; set; } = "Félicitations ! Note {note}/{total}. Permis délivré.";


	public string Echec { get; set; } = "Échec : {note}/{total} (minimum {min}).";


	public string PermisRetire { get; set; } = "Votre permis de port d'arme a été retiré.";

}
