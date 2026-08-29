using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;

using Hyperlane.Core;

namespace MasteringLanguage
{
	using System.Collections.Immutable;
	using System.Runtime.InteropServices;

	public sealed class CargoHold
	{
		private readonly Dictionary<Resource, long> _contenu = new();

		public static long Capacity(Fleet fleet)
		{
			var capacity = 0L;
			foreach (var ship in fleet.Ships) capacity += ship.CargoCapacity;   // pas de LINQ : pas d'itérateur
			return capacity;
		}

		public bool Load(Fleet fleet, params ReadOnlySpan<ResourceStock> stocks)
		{
			// Un span ne se capture pas dans une lambda : on somme à la main
			var demande = 0L;
			foreach (var stock in stocks)
			{
				if (stock.Quantity < 0) throw new ArgumentException("Quantité négative interdite.", nameof(stocks));
				demande += stock.Quantity;
			}

			if (demande > Capacity(fleet) - TotalCharge()) return false;

			foreach (var stock in stocks)
			{
				// GetValueRefOrAddDefault : un seul hash au lieu de TryGetValue + indexeur
				ref var quantite = ref CollectionsMarshal.GetValueRefOrAddDefault(_contenu, stock.Resource, out _);
				quantite += stock.Quantity;
			}

			return true;
		}

		public ImmutableArray<ResourceStock> BuildResupplyManifest(IEnumerable<ResourceStock> demandeLocale)
		{
			//var enSoute = new ResourceStock[_contenu.Count];
			//var i = 0;
			//foreach (var (resource, quantity) in _contenu) enSoute[i++] = new ResourceStock(resource, quantity);

			//// Une seule collection expression : le compilateur cible le CollectionBuilder d'ImmutableArray
			//return [.. enSoute, .. demandeLocale, new ResourceStock(Resource.Vivres, 50)];

			var builder = ImmutableArray.CreateBuilder<ResourceStock>(_contenu.Count + 1);
			foreach (var (resource, quantity) in _contenu)
				builder.Add(new ResourceStock(resource, quantity));
			builder.AddRange(demandeLocale);
			builder.Add(new ResourceStock(Resource.Vivres, 50));
			return builder.DrainToImmutable();
		}

		private long TotalCharge()
		{
			var total = 0L;
			foreach (var quantite in _contenu.Values) total += quantite;
			return total;
		}
	}
}
