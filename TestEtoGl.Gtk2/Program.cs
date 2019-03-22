using Eto.Gl;
using Eto.Forms;
using OpenTK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TestEtoGl.Gtk2
{
	static class Program
	{
		/// <summary>
		/// The main entry point for the application.
		/// </summary>
		[STAThread]
		static void Main()
		{
			Toolkit.Init(new ToolkitOptions { Backend = PlatformBackend.PreferNative });

			var platform = new Eto.GtkSharp.Platform();
			platform.Add<GLSurface.IHandler>(() => new Eto.Gl.Gtk.GtkGlSurfaceHandler());

			new Application(platform).Run(new MainForm());
		}
	}
}
