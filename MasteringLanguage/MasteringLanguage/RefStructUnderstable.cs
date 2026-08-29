using System;
using System.Collections.Generic;
using System.Text;

namespace MasteringLanguage
{
	ref struct PlanetTest
	{
		public int id;
	}
	
	public class TestStruct
	{
		public static void Test()
		{
			var testOne = (ref PlanetTest test) =>
			{
				test.id++;
			};

			var planet = new PlanetTest();
			testOne(ref planet);

			Console.WriteLine(planet.id);
		}	
	}
}
