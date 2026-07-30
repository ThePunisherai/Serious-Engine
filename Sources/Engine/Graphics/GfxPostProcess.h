/* Copyright (c) 2002-2012 Croteam Ltd.
This program is free software; you can redistribute it and/or modify
it under the terms of version 2 of the GNU General Public License as published by
the Free Software Foundation


This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License along
with this program; if not, write to the Free Software Foundation, Inc.,
51 Franklin Street, Fifth Floor, Boston, MA 02110-1301 USA. */

#ifndef SE_INCL_GFXPOSTPROCESS_H
#define SE_INCL_GFXPOSTPROCESS_H
#ifdef PRAGMA_ONCE
  #pragma once
#endif

/*
 * Opt-in modern post-processing for the OpenGL renderer.
 *
 * The scene renderer stays exactly as it is: fixed-function multitexturing, no shaders. This runs
 * afterwards, on the finished frame, which is what makes it usable on content built for the
 * original engine without touching that content or the renderer that draws it.
 *
 * The frame is read back out of the back buffer with glCopyTexSubImage2D rather than being rendered
 * into a framebuffer object, so nothing about how the scene is drawn has to change and no FBO
 * support is required -- only GLSL, which is GL 2.0. Intermediate passes draw into the back buffer
 * and are copied straight back out again; the buffer is not presented until the chain has finished,
 * so using it as scratch space is free.
 *
 * Everything is off unless gfx_bPostProcessing is set, and the whole stage disables itself if the
 * driver has no shader support.
 */

/*
 * The scene's projection, recorded by ogl_SetFrustum as the world is drawn.
 *
 * Ambient occlusion has to turn a depth sample back into a view-space position, which needs the
 * frustum the scene was drawn with. It cannot be read back at the end of the frame: the 2D overlay
 * is drawn after the world and leaves its own orthographic projection behind.
 *
 * The tangents are half-extents at unit depth, so a pixel at normalized device coordinates
 * (x, y) and view depth z sits at (x * tanX * z, y * tanY * z, -z).
 */
extern FLOAT _fPostSceneNear;
extern FLOAT _fPostSceneFar;
extern FLOAT _fPostSceneTanX;
extern FLOAT _fPostSceneTanY;
extern BOOL  _bPostSceneFrustumValid;

// Runs the post-processing chain over the finished frame, immediately before it is presented.
// Does nothing unless enabled, the current API is OpenGL, and the driver supports GLSL.
void PostProcessFrame(void);

// Releases shaders and scratch textures. Safe to call when nothing was ever initialized.
void ShutdownPostProcessing(void);

#endif  /* include-once check. */
