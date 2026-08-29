using System;
using System.Collections.Generic;
using System.Text;

namespace MasteringLanguage
{
	public struct Compteur
	{
		private int _n;
		public int Valeur => _n;
		public void Incr() => _n++;      // membre NON-readonly
	}

	//public readonly struct Compteur
	//{
	//	private int _n;
	//	public int Valeur => _n;
	//	public void Incr() => _n++;      // membre NON-readonly
	//}

	class Service
	{
		private readonly Compteur _c;

		public void Faire()
		{
			_c.Incr();               // compile... mais _c n'est PAS modifié
			Console.WriteLine(_c.Valeur);  // 0
		}
	}
}
