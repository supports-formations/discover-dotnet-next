# 🔬 Guide d'exécution des Benchmarks

## 1️⃣ Compilation en RELEASE (OBLIGATOIRE)

Les benchmarks doivent être exécutés en mode **Release** pour avoir des résultats fiables :

```powershell
cd C:\Users\evan\Documents\Works\formations\dotnet\discover-dotnet-next\MasteringLanguage\MasteringLanguage
dotnet build -c Release
dotnet run -c Release
```

⚠️ **NE JAMAIS** benchmarker en mode Debug ! Les optimisations sont désactivées.

---

## 2️⃣ Lire les résultats

Après l'exécution, vous verrez un tableau comme :

```
| Method                    | Mean      | Error    | StdDev   | Allocated |
|-------------------------- |----------:|---------:|---------:|----------:|
| NormalStruct_Processing   | 120.5 μs  | 2.1 μs   | 1.9 μs   |       -   |
| RefStruct_Processing      | 118.2 μs  | 1.8 μs   | 1.7 μs   |       -   |
| NormalStruct_WithBoxing   | 850.3 μs  | 12.4 μs  | 11.6 μs  | 400000 B  |
| WithSpan_RefStruct        | 25.8 μs   | 0.5 μs   | 0.4 μs   |       -   |
| WithArray_NormalStruct    | 45.2 μs   | 0.9 μs   | 0.8 μs   |    424 B  |
```

### 📖 Interprétation :

- **Mean** : Temps moyen d'exécution (plus petit = mieux)
- **Error/StdDev** : Marge d'erreur (plus petit = plus fiable)
- **Allocated** : Mémoire allouée sur le heap
  - `-` ou `0 B` = Zéro allocation (excellent !)
  - `424 B` = 424 bytes alloués

---

## 3️⃣ Ce que prouvent les benchmarks

### Test 1 & 2 : Processing simple
- **Différence minime** car petites structs
- Sur stack dans les deux cas

### Test 3 : WithBoxing
- **NormalStruct** : ⚠️ Boxing = allocations massives
- **RefStruct** : ❌ Impossible de boxer (erreur compilation)

### Test 4 & 5 : Span vs Array
- **WithSpan_RefStruct** : 
  - ✅ `stackalloc` = zero allocation
  - ✅ Plus rapide

- **WithArray_NormalStruct** :
  - ❌ `new int[]` = allocation heap
  - ❌ Garbage Collector activé

---

## 4️⃣ Commandes utiles

### Exporter les résultats
```powershell
dotnet run -c Release --exporters json html
```
Génère des fichiers HTML et JSON avec les résultats

### Voir uniquement un benchmark
Modifiez la classe pour ajouter un filtre :
```csharp
[DryJob]  // Test rapide
public class StructBenchmarks { ... }
```

---

## 5️⃣ Résumé des gains

| Scénario | Struct normale | Ref struct | Gain |
|----------|---------------|------------|------|
| Simple processing | OK | OK | Minimal |
| Avec Span<T> | ❌ | ✅ | Zero alloc |
| Avec stackalloc | Possible | Optimal | Zero alloc |
| Boxing fréquent | ⚠️ Lent | ❌ Impossible* | N/A |

\* Mais c'est une **bonne chose** - ça évite les erreurs de performance

---

## 6️⃣ Recommandations

✅ **Utilisez ref struct** si :
- Vous utilisez `Span<T>` ou `Memory<T>`
- Performance critique avec données temporaires
- Vous voulez garantir zero allocation

❌ **N'utilisez PAS ref struct** si :
- Besoin de boxing/interfaces
- Utilisation dans async/await
- Pas de contrainte de performance
