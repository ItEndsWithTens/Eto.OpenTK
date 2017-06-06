﻿using Eto.Drawing;
using Eto.Forms;
using Eto.Wpf.Forms;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;
using System;
using OpenTK.Graphics.OpenGL;

namespace Eto.Gl.WPF_WFControl
{
	public class WPFWFGLSurfaceHandler : WpfControl<OtkWpfWFControl, GLSurface, GLSurface.ICallback>, GLSurface.IHandler
	{
		public void CreateWithParams(GraphicsMode mode, int major, int minor, GraphicsContextFlags flags)
		{
			Control = new OtkWpfWFControl(mode, major, minor, flags);
		}

		protected override void Initialize()
		{
			base.Initialize();
			HandleEvent(GLSurface.GLDrawEvent);
		}

		public bool IsInitialized
		{
			get { return Control.IsInitialized; }
		}

		public void MakeCurrent()
		{
			Control.MakeCurrent();
		}

		public void SwapBuffers()
		{
			Control.SwapBuffers();
		}

		public override void AttachEvent(string id)
		{
			switch (id)
			{
				case GLSurface.GLInitializedEvent:
					Control.Initialized += (sender, e) => Callback.OnInitialized(Widget, EventArgs.Empty);
					break;

				case GLSurface.GLShuttingDownEvent:
					//Control.ShuttingDown += (sender, e) => Callback.OnShuttingDown(Widget, e);
					break;

				case GLSurface.GLDrawEvent:
					//Control.Resize += (sender, e) => Callback.OnDraw(Widget, EventArgs.Empty);
					break;

				default:
					base.AttachEvent(id);
					break;
			}
		}
	}
}