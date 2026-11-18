Console.WriteLine("Hello, World!");

var rapport = "KES-01;1000000;500;120;300\nVHR-07;2400000;80;900;40\n";

ReadOnlySpan<char> restant = rapport;

while (!restant.IsEmpty)
{
    var fin = restant.IndexOf('\n');
    var ligne = fin < 0 ? restant : restant[..fin];   // AUCUNE allocation
    restant = fin < 0 ? default : restant[(fin + 1)..];

    // ligne pointe DANS la string d'origine, à un décalage donné
    var pv = ligne.IndexOf(';');
    var code = ligne[..pv];                            // toujours aucune allocation
}