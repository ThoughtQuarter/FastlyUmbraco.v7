using System;
using System.Xml;
using umbraco.interfaces;

namespace FastlyUmbraco.v7
{
	class AddFastlyWebConfigsPackageAction : IPackageAction
	{
		public string Alias() => "AddFastlyWebConfigsPackageAction";

		public bool Execute(string packageName, XmlNode xmlData)
		{
			FastlyUmbraco.CreateWebConfigSettings();
			return true;
		}

		public XmlNode SampleXml()
		{
			var xml = string.Format("<Action runat=\"install\" undo=\"true\" alias=\"{0}\" />", Alias());
			XmlDocument x = new XmlDocument();
			x.LoadXml(xml);
			return x;
		}

		public bool Undo(string packageName, XmlNode xmlData)
		{
			FastlyUmbraco.RemoveWebConfigSettings();
			return true;
		}
	}
}
