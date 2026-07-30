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

#include "stdh.h"

#include <Engine/Graphics/GfxPostProcess.h>
#include <Engine/Graphics/GfxLibrary.h>
#include <Engine/Graphics/Gfx_wrapper.h>
#include <Engine/Graphics/OpenGL.h>
#include <Engine/Graphics/ViewPort.h>
#include <Engine/Graphics/Color.h>
#include <Engine/Graphics/Vertex.h>
#include <Engine/Base/Console.h>
#include <Engine/Base/Translation.h>
#include <Engine/Math/Functions.h>

// exposed controls, declared and registered in GfxLibrary.cpp
extern INDEX gfx_bPostProcessing;
extern INDEX gfx_bFXAA;
extern INDEX gfx_iTonemap;
extern FLOAT gfx_fExposure;
extern FLOAT gfx_fPostSaturation;
extern FLOAT gfx_fBloomThreshold;
extern FLOAT gfx_fBloomIntensity;
extern INDEX gfx_bSSAO;
extern FLOAT gfx_fSSAORadius;
extern FLOAT gfx_fSSAOIntensity;

// the scene projection, filled in by ogl_SetFrustum
FLOAT _fPostSceneNear  = 0.0f;
FLOAT _fPostSceneFar   = 0.0f;
FLOAT _fPostSceneTanX  = 0.0f;
FLOAT _fPostSceneTanY  = 0.0f;
BOOL  _bPostSceneFrustumValid = FALSE;


// GLSL entry points. These are GL 2.0 rather than extensions, but the engine links against the
// GL 1.1 import library that ships with Windows, so they still have to come through
// wglGetProcAddress like any other post-1.1 function.
typedef GLuint (__stdcall *PFN_CreateShader)(GLenum);
typedef void   (__stdcall *PFN_ShaderSource)(GLuint, GLsizei, const char**, const GLint*);
typedef void   (__stdcall *PFN_CompileShader)(GLuint);
typedef void   (__stdcall *PFN_GetShaderiv)(GLuint, GLenum, GLint*);
typedef void   (__stdcall *PFN_GetShaderInfoLog)(GLuint, GLsizei, GLsizei*, char*);
typedef void   (__stdcall *PFN_DeleteShader)(GLuint);
typedef GLuint (__stdcall *PFN_CreateProgram)(void);
typedef void   (__stdcall *PFN_AttachShader)(GLuint, GLuint);
typedef void   (__stdcall *PFN_LinkProgram)(GLuint);
typedef void   (__stdcall *PFN_GetProgramiv)(GLuint, GLenum, GLint*);
typedef void   (__stdcall *PFN_GetProgramInfoLog)(GLuint, GLsizei, GLsizei*, char*);
typedef void   (__stdcall *PFN_UseProgram)(GLuint);
typedef void   (__stdcall *PFN_DeleteProgram)(GLuint);
typedef GLint  (__stdcall *PFN_GetUniformLocation)(GLuint, const char*);
typedef void   (__stdcall *PFN_Uniform1i)(GLint, GLint);
typedef void   (__stdcall *PFN_Uniform1f)(GLint, GLfloat);
typedef void   (__stdcall *PFN_Uniform2f)(GLint, GLfloat, GLfloat);

// Cast, because GLenum in gl_types.h is an enumeration type rather than an unsigned int,
// and C++ will not convert a plain integer constant to one implicitly.
#define GL_FRAGMENT_SHADER_ ((GLenum)0x8B30)
#define GL_VERTEX_SHADER_   ((GLenum)0x8B31)
#define GL_COMPILE_STATUS_  ((GLenum)0x8B81)
#define GL_LINK_STATUS_     ((GLenum)0x8B82)

static PFN_CreateShader       pCreateShader       = NULL;
static PFN_ShaderSource       pShaderSource       = NULL;
static PFN_CompileShader      pCompileShader      = NULL;
static PFN_GetShaderiv        pGetShaderiv        = NULL;
static PFN_GetShaderInfoLog   pGetShaderInfoLog   = NULL;
static PFN_DeleteShader       pDeleteShader       = NULL;
static PFN_CreateProgram      pCreateProgram      = NULL;
static PFN_AttachShader       pAttachShader       = NULL;
static PFN_LinkProgram        pLinkProgram        = NULL;
static PFN_GetProgramiv       pGetProgramiv       = NULL;
static PFN_GetProgramInfoLog  pGetProgramInfoLog  = NULL;
static PFN_UseProgram         pUseProgram         = NULL;
static PFN_DeleteProgram      pDeleteProgram      = NULL;
static PFN_GetUniformLocation pGetUniformLocation = NULL;
static PFN_Uniform1i          pUniform1i          = NULL;
static PFN_Uniform1f          pUniform1f          = NULL;
static PFN_Uniform2f          pUniform2f          = NULL;


// -1 not tried yet, 0 unavailable, 1 ready
static INDEX _iPostState = -1;

static GLuint _uiSceneTexture = 0;   // the frame as it came out of the scene renderer
static GLuint _uiWorkTexture  = 0;   // result of the previous pass, at full resolution
static GLuint _uiBloomA       = 0;   // half-resolution bloom ping
static GLuint _uiBloomB       = 0;   // half-resolution bloom pong

static PIX _pixWidth  = 0;
static PIX _pixHeight = 0;

static GLuint _uiDepthTexture = 0;   // scene depth, full resolution
static GLuint _uiAoA          = 0;   // half-resolution occlusion ping
static GLuint _uiAoB          = 0;   // half-resolution occlusion pong
static BOOL   _bDepthUsable   = TRUE; // cleared if the driver refuses a depth texture

static GLuint _uiSsaoProgram     = 0;
static GLuint _uiBrightProgram   = 0;
static GLuint _uiBlurProgram     = 0;
static GLuint _uiCompositeProgram = 0;
static GLuint _uiFxaaProgram     = 0;


// Passed through unchanged; the quad is drawn through the engine's own vertex arrays, so the
// fixed-function transform is exactly what every other draw in the frame uses.
static const char *_strVertexShader =
  "#version 110\n"
  "void main() {\n"
  "  gl_TexCoord[0] = gl_MultiTexCoord0;\n"
  "  gl_Position = ftransform();\n"
  "}\n";

// Screen-space ambient occlusion from the depth buffer.
//
// The kernel is generated as a spiral from the loop counter rather than read out of a const array,
// because indexing an array with a loop variable is not something GLSL 110 guarantees. Each pixel
// rotates the spiral by a hash of its coordinates, which trades banding for noise -- the blur pass
// afterwards is what turns that noise back into a smooth term.
static const char *_strSsaoShader =
  "#version 110\n"
  "uniform sampler2D texDepth;\n"
  "uniform float fNear;\n"
  "uniform float fFar;\n"
  "uniform vec2 vTan;\n"
  "uniform float fRadius;\n"
  "uniform float fIntensity;\n"
  "float ViewDepth(vec2 vUV) {\n"
  "  float fD = texture2D(texDepth, vUV).r;\n"
  "  return (2.0 * fNear * fFar) / (fFar + fNear - (2.0 * fD - 1.0) * (fFar - fNear));\n"
  "}\n"
  "vec3 ViewPos(vec2 vUV) {\n"
  "  float fZ = ViewDepth(vUV);\n"
  "  return vec3((vUV.x * 2.0 - 1.0) * vTan.x * fZ, (vUV.y * 2.0 - 1.0) * vTan.y * fZ, -fZ);\n"
  "}\n"
  "void main() {\n"
  "  vec2 vUV = gl_TexCoord[0].st;\n"
  "  vec3 vP = ViewPos(vUV);\n"
  "  float fZ = -vP.z;\n"
  // Anything on the far plane is sky: there is no surface there to occlude.
  "  if (fZ >= fFar * 0.99) { gl_FragColor = vec4(1.0); return; }\n"
  "  vec3 vN = normalize(cross(dFdx(vP), dFdy(vP)));\n"
  "  float fAngle = fract(sin(dot(vUV, vec2(12.9898, 78.233))) * 43758.5453) * 6.2831853;\n"
  // A view-space offset of fRadius at depth fZ spans this much of the 0..1 texture.
  "  vec2 vScale = vec2(fRadius / max(vTan.x * fZ, 0.0001),\n"
  "                     fRadius / max(vTan.y * fZ, 0.0001)) * 0.5;\n"
  "  float fOcclusion = 0.0;\n"
  "  for (int i = 0; i < 12; i++) {\n"
  "    float fT = (float(i) + 0.5) / 12.0;\n"
  "    float fA = fAngle + fT * 18.8495559;\n"
  "    vec2 vSampleUV = vUV + vec2(cos(fA), sin(fA)) * fT * vScale;\n"
  "    vec3 vDiff = ViewPos(vSampleUV) - vP;\n"
  "    float fLen = length(vDiff);\n"
  "    if (fLen > 0.0001) {\n"
  "      float fFacing = max(dot(vN, vDiff / fLen), 0.0);\n"
  // Falls off with distance so a wall across the room does not shade the floor under your feet.
  "      fOcclusion += max(fFacing - 0.02, 0.0) * (fRadius / (fRadius + fLen));\n"
  "    }\n"
  "  }\n"
  "  float fAO = clamp(1.0 - (fOcclusion / 12.0) * fIntensity, 0.0, 1.0);\n"
  "  gl_FragColor = vec4(fAO, fAO, fAO, 1.0);\n"
  "}\n";

// Isolates the part of the image bright enough to bleed, with a soft knee so the bloom fades in
// across the threshold instead of switching on at a visible contour.
static const char *_strBrightShader =
  "#version 110\n"
  "uniform sampler2D texScene;\n"
  "uniform float fThreshold;\n"
  "void main() {\n"
  "  vec3 vColor = texture2D(texScene, gl_TexCoord[0].st).rgb;\n"
  "  float fLuma = dot(vColor, vec3(0.2126, 0.7152, 0.0722));\n"
  "  float fKnee = max(fLuma - fThreshold, 0.0) / max(fLuma, 0.0001);\n"
  "  gl_FragColor = vec4(vColor * fKnee, 1.0);\n"
  "}\n";

// One half of a separable gaussian. vDirection carries the texel step, so the same program serves
// both the horizontal and the vertical pass.
static const char *_strBlurShader =
  "#version 110\n"
  "uniform sampler2D texSource;\n"
  "uniform vec2 vDirection;\n"
  "void main() {\n"
  "  vec2 vUV = gl_TexCoord[0].st;\n"
  "  vec3 vSum = texture2D(texSource, vUV).rgb * 0.227027;\n"
  "  vSum += texture2D(texSource, vUV + vDirection * 1.384615).rgb * 0.316216;\n"
  "  vSum += texture2D(texSource, vUV - vDirection * 1.384615).rgb * 0.316216;\n"
  "  vSum += texture2D(texSource, vUV + vDirection * 3.230769).rgb * 0.070270;\n"
  "  vSum += texture2D(texSource, vUV - vDirection * 3.230769).rgb * 0.070270;\n"
  "  gl_FragColor = vec4(vSum, 1.0);\n"
  "}\n";

// Exposure, bloom, the filmic curve and saturation, in that order. The curve is the ACES fit,
// which is what stops the highlights from clipping to flat white the moment exposure is raised.
static const char *_strCompositeShader =
  "#version 110\n"
  "uniform sampler2D texScene;\n"
  "uniform sampler2D texBloom;\n"
  "uniform sampler2D texAO;\n"
  "uniform float fExposure;\n"
  "uniform float fBloomIntensity;\n"
  "uniform float fSaturation;\n"
  "uniform int iTonemap;\n"
  "uniform int iUseAO;\n"
  "vec3 Aces(vec3 v) {\n"
  "  return clamp((v * (2.51 * v + 0.03)) / (v * (2.43 * v + 0.59) + 0.14), 0.0, 1.0);\n"
  "}\n"
  "void main() {\n"
  "  vec3 vColor = texture2D(texScene, gl_TexCoord[0].st).rgb;\n"
  // Occlusion darkens the scene before the highlights bleed, so an unlit corner does not glow.
  "  if (iUseAO != 0) vColor *= texture2D(texAO, gl_TexCoord[0].st).r;\n"
  "  vColor += texture2D(texBloom, gl_TexCoord[0].st).rgb * fBloomIntensity;\n"
  "  vColor *= fExposure;\n"
  "  if (iTonemap != 0) vColor = Aces(vColor);\n"
  "  float fLuma = dot(vColor, vec3(0.2126, 0.7152, 0.0722));\n"
  "  vColor = clamp(mix(vec3(fLuma), vColor, fSaturation), 0.0, 1.0);\n"
  "  gl_FragColor = vec4(vColor, 1.0);\n"
  "}\n";

// Cheap edge-directed blur along luminance gradients. Not full FXAA, but it costs one pass and
// takes the stair-stepping off geometry edges, which is most of what the original renderer shows.
static const char *_strFxaaShader =
  "#version 110\n"
  "uniform sampler2D texSource;\n"
  "uniform vec2 vTexelSize;\n"
  "float Luma(vec3 v) { return dot(v, vec3(0.2126, 0.7152, 0.0722)); }\n"
  "void main() {\n"
  "  vec2 vUV = gl_TexCoord[0].st;\n"
  "  float fM  = Luma(texture2D(texSource, vUV).rgb);\n"
  "  float fNW = Luma(texture2D(texSource, vUV + vec2(-vTexelSize.x, -vTexelSize.y)).rgb);\n"
  "  float fNE = Luma(texture2D(texSource, vUV + vec2( vTexelSize.x, -vTexelSize.y)).rgb);\n"
  "  float fSW = Luma(texture2D(texSource, vUV + vec2(-vTexelSize.x,  vTexelSize.y)).rgb);\n"
  "  float fSE = Luma(texture2D(texSource, vUV + vec2( vTexelSize.x,  vTexelSize.y)).rgb);\n"
  "  float fMin = min(fM, min(min(fNW, fNE), min(fSW, fSE)));\n"
  "  float fMax = max(fM, max(max(fNW, fNE), max(fSW, fSE)));\n"
  "  if (fMax - fMin < 0.05) { gl_FragColor = texture2D(texSource, vUV); return; }\n"
  "  vec2 vGradient = vec2(-((fNW + fNE) - (fSW + fSE)), ((fNW + fSW) - (fNE + fSE)));\n"
  // Only the centre texel differing leaves the corner gradient at zero while the contrast test
  // still passes; normalizing that would hand back NaN and punch a hole in the image.
  "  if (dot(vGradient, vGradient) < 1e-8) { gl_FragColor = texture2D(texSource, vUV); return; }\n"
  "  vec2 vDir = clamp(normalize(vGradient), -2.0, 2.0) * vTexelSize;\n"
  "  vec3 vA = 0.5 * (texture2D(texSource, vUV + vDir * (1.0 / 3.0 - 0.5)).rgb\n"
  "                 + texture2D(texSource, vUV + vDir * (2.0 / 3.0 - 0.5)).rgb);\n"
  "  vec3 vB = vA * 0.5 + 0.25 * (texture2D(texSource, vUV - vDir * 0.5).rgb\n"
  "                             + texture2D(texSource, vUV + vDir * 0.5).rgb);\n"
  "  float fLumaB = Luma(vB);\n"
  "  gl_FragColor = vec4((fLumaB < fMin || fLumaB > fMax) ? vA : vB, 1.0);\n"
  "}\n";


static BOOL LoadEntryPoints(void)
{
  #define GETPROC(var, type, name) \
    var = (type)pwglGetProcAddress(name); \
    if (var == NULL) { CPrintF(TRANS("Post-processing: driver has no %s\n"), name); return FALSE; }

  GETPROC(pCreateShader,       PFN_CreateShader,       "glCreateShader");
  GETPROC(pShaderSource,       PFN_ShaderSource,       "glShaderSource");
  GETPROC(pCompileShader,      PFN_CompileShader,      "glCompileShader");
  GETPROC(pGetShaderiv,        PFN_GetShaderiv,        "glGetShaderiv");
  GETPROC(pGetShaderInfoLog,   PFN_GetShaderInfoLog,   "glGetShaderInfoLog");
  GETPROC(pDeleteShader,       PFN_DeleteShader,       "glDeleteShader");
  GETPROC(pCreateProgram,      PFN_CreateProgram,      "glCreateProgram");
  GETPROC(pAttachShader,       PFN_AttachShader,       "glAttachShader");
  GETPROC(pLinkProgram,        PFN_LinkProgram,        "glLinkProgram");
  GETPROC(pGetProgramiv,       PFN_GetProgramiv,       "glGetProgramiv");
  GETPROC(pGetProgramInfoLog,  PFN_GetProgramInfoLog,  "glGetProgramInfoLog");
  GETPROC(pUseProgram,         PFN_UseProgram,         "glUseProgram");
  GETPROC(pDeleteProgram,      PFN_DeleteProgram,      "glDeleteProgram");
  GETPROC(pGetUniformLocation, PFN_GetUniformLocation, "glGetUniformLocation");
  GETPROC(pUniform1i,          PFN_Uniform1i,          "glUniform1i");
  GETPROC(pUniform1f,          PFN_Uniform1f,          "glUniform1f");
  GETPROC(pUniform2f,          PFN_Uniform2f,          "glUniform2f");
  #undef GETPROC

  return TRUE;
}


static GLuint CompileStage(GLenum eType, const char *strSource, const char *strName)
{
  GLuint uiShader = pCreateShader(eType);
  if (uiShader == 0) return 0;

  pShaderSource(uiShader, 1, &strSource, NULL);
  pCompileShader(uiShader);

  GLint iCompiled = 0;
  pGetShaderiv(uiShader, GL_COMPILE_STATUS_, &iCompiled);
  if (!iCompiled) {
    char achLog[1024];
    achLog[0] = '\0';
    pGetShaderInfoLog(uiShader, sizeof(achLog) - 1, NULL, achLog);
    CPrintF(TRANS("Post-processing: '%s' failed to compile: %s\n"), strName, achLog);
    pDeleteShader(uiShader);
    return 0;
  }
  return uiShader;
}


static GLuint BuildProgram(const char *strFragment, const char *strName)
{
  GLuint uiVertex = CompileStage(GL_VERTEX_SHADER_, _strVertexShader, "vertex");
  if (uiVertex == 0) return 0;

  GLuint uiFragment = CompileStage(GL_FRAGMENT_SHADER_, strFragment, strName);
  if (uiFragment == 0) {
    pDeleteShader(uiVertex);
    return 0;
  }

  GLuint uiProgram = pCreateProgram();
  pAttachShader(uiProgram, uiVertex);
  pAttachShader(uiProgram, uiFragment);
  pLinkProgram(uiProgram);

  // The program keeps them alive; dropping our references here means the driver frees them with it.
  pDeleteShader(uiVertex);
  pDeleteShader(uiFragment);

  GLint iLinked = 0;
  pGetProgramiv(uiProgram, GL_LINK_STATUS_, &iLinked);
  if (!iLinked) {
    char achLog[1024];
    achLog[0] = '\0';
    pGetProgramInfoLog(uiProgram, sizeof(achLog) - 1, NULL, achLog);
    CPrintF(TRANS("Post-processing: '%s' failed to link: %s\n"), strName, achLog);
    pDeleteProgram(uiProgram);
    return 0;
  }
  return uiProgram;
}


// Allocates a scratch target. Bound raw on purpose: gfxSetTexture() binds unconditionally, so it
// keeps no binding cache for this to invalidate.
static void CreateTarget(GLuint &uiTexture, PIX pixW, PIX pixH)
{
  if (uiTexture == 0) pglGenTextures(1, &uiTexture);
  pglBindTexture(GL_TEXTURE_2D, uiTexture);
  pglTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
  pglTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
  pglTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
  pglTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
  pglTexImage2D(GL_TEXTURE_2D, 0, GL_RGB8, pixW, pixH, 0, GL_RGB, GL_UNSIGNED_BYTE, NULL);
  OGL_CHECKERROR;
}


static void ReleaseTexture(GLuint &uiTexture)
{
  if (uiTexture == 0) return;
  pglDeleteTextures(1, &uiTexture);
  uiTexture = 0;
}


// Same as CreateTarget, but for the depth buffer. The base internal format is used rather than a
// sized one so the driver picks whatever precision its depth buffer already has; a driver with no
// depth-texture support fails here and switches occlusion off for the session.
static BOOL CreateDepthTarget(GLuint &uiTexture, PIX pixW, PIX pixH)
{
  if (uiTexture == 0) pglGenTextures(1, &uiTexture);
  pglBindTexture(GL_TEXTURE_2D, uiTexture);
  pglTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
  pglTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
  pglTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
  pglTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

  // Asked for without the engine's error check, because a refusal here is expected and handled.
  pglTexImage2D(GL_TEXTURE_2D, 0, GL_DEPTH_COMPONENT, pixW, pixH, 0,
                GL_DEPTH_COMPONENT, GL_UNSIGNED_BYTE, NULL);
  if (pglGetError() != 0) {
    CPrintF(TRANS("Post-processing: no depth texture support, ambient occlusion disabled.\n"));
    ReleaseTexture(uiTexture);
    return FALSE;
  }
  return TRUE;
}


void ShutdownPostProcessing(void)
{
  if (_iPostState == 1) {
    if (pDeleteProgram != NULL) {
      if (_uiBrightProgram    != 0) pDeleteProgram(_uiBrightProgram);
      if (_uiBlurProgram      != 0) pDeleteProgram(_uiBlurProgram);
      if (_uiCompositeProgram != 0) pDeleteProgram(_uiCompositeProgram);
      if (_uiFxaaProgram      != 0) pDeleteProgram(_uiFxaaProgram);
      if (_uiSsaoProgram      != 0) pDeleteProgram(_uiSsaoProgram);
    }
    ReleaseTexture(_uiSceneTexture);
    ReleaseTexture(_uiWorkTexture);
    ReleaseTexture(_uiBloomA);
    ReleaseTexture(_uiBloomB);
    ReleaseTexture(_uiDepthTexture);
    ReleaseTexture(_uiAoA);
    ReleaseTexture(_uiAoB);
  }

  _uiBrightProgram = _uiBlurProgram = _uiCompositeProgram = _uiFxaaProgram = 0;
  _uiSsaoProgram = 0;
  _bDepthUsable = TRUE;
  _pixWidth = _pixHeight = 0;
  _iPostState = -1;
}


// Builds programs once, and (re)allocates targets whenever the back buffer changes size.
static BOOL EnsureReady(PIX pixW, PIX pixH)
{
  if (_iPostState == 0) return FALSE;

  if (_iPostState == -1) {
    if (!LoadEntryPoints()) {
      CPrintF(TRANS("Post-processing unavailable: driver does not support GLSL.\n"));
      _iPostState = 0;
      return FALSE;
    }

    _uiBrightProgram    = BuildProgram(_strBrightShader,    "bright pass");
    _uiBlurProgram      = BuildProgram(_strBlurShader,      "blur");
    _uiCompositeProgram = BuildProgram(_strCompositeShader, "composite");
    _uiFxaaProgram      = BuildProgram(_strFxaaShader,      "antialias");

    // Occlusion is optional: if its shader will not build, the rest of the chain still runs.
    _uiSsaoProgram = BuildProgram(_strSsaoShader, "ambient occlusion");
    if (_uiSsaoProgram == 0) _bDepthUsable = FALSE;

    if (_uiBrightProgram == 0 || _uiBlurProgram == 0 || _uiCompositeProgram == 0 || _uiFxaaProgram == 0) {
      _iPostState = 1;        // so Shutdown releases whatever did build
      ShutdownPostProcessing();
      _iPostState = 0;
      return FALSE;
    }

    _iPostState = 1;
    CPrintF(TRANS("Post-processing ready (bloom, tonemapping, antialiasing).\n"));
  }

  if (pixW != _pixWidth || pixH != _pixHeight) {
    CreateTarget(_uiSceneTexture, pixW, pixH);
    CreateTarget(_uiWorkTexture,  pixW, pixH);
    CreateTarget(_uiBloomA, pixW / 2, pixH / 2);
    CreateTarget(_uiBloomB, pixW / 2, pixH / 2);

    if (_bDepthUsable) {
      CreateTarget(_uiAoA, pixW / 2, pixH / 2);
      CreateTarget(_uiAoB, pixW / 2, pixH / 2);
      _bDepthUsable = CreateDepthTarget(_uiDepthTexture, pixW, pixH);
    }
    _pixWidth  = pixW;
    _pixHeight = pixH;
  }
  return TRUE;
}


// Copies the lower-left pixW x pixH of the back buffer into a target.
static void CaptureInto(GLuint uiTexture, PIX pixW, PIX pixH)
{
  pglBindTexture(GL_TEXTURE_2D, uiTexture);
  pglCopyTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, 0, 0, pixW, pixH);
  OGL_CHECKERROR;
}


// Draws a unit quad covering the viewport, through the engine's own arrays so the state cache
// stays in step with what the driver actually has bound.
static void DrawFullScreenQuad(void)
{
  static GFXVertex   avtx[4];
  static GFXTexCoord atex[4];
  static INDEX aidx[6] = { 0, 1, 2, 0, 2, 3 };

  avtx[0].x = 0.0f; avtx[0].y = 0.0f; avtx[0].z = 0.5f;
  avtx[1].x = 1.0f; avtx[1].y = 0.0f; avtx[1].z = 0.5f;
  avtx[2].x = 1.0f; avtx[2].y = 1.0f; avtx[2].z = 0.5f;
  avtx[3].x = 0.0f; avtx[3].y = 1.0f; avtx[3].z = 0.5f;

  atex[0].u = 0.0f; atex[0].v = 0.0f;
  atex[1].u = 1.0f; atex[1].v = 0.0f;
  atex[2].u = 1.0f; atex[2].v = 1.0f;
  atex[3].u = 0.0f; atex[3].v = 1.0f;

  gfxSetVertexArray(avtx, 4);
  gfxSetTexCoordArray(atex, FALSE);
  gfxDrawElements(6, aidx);
}


static void SetSampler(GLuint uiProgram, const char *strName, INDEX iUnit, GLuint uiTexture)
{
  gfxSetTextureUnit(iUnit);
  pglBindTexture(GL_TEXTURE_2D, uiTexture);
  pUniform1i(pGetUniformLocation(uiProgram, strName), iUnit);
}


void PostProcessFrame(void)
{
  if (!gfx_bPostProcessing) return;
  if (_pGfx->gl_eCurrentAPI != GAT_OGL) return;

  CViewPort *pvp = _pGfx->gl_pvpActive;
  if (pvp == NULL) return;

  const PIX pixW = pvp->vp_Raster.ra_Width;
  const PIX pixH = pvp->vp_Raster.ra_Height;
  if (pixW < 4 || pixH < 4) return;
  if (!EnsureReady(pixW, pixH)) return;

  // clamp the controls the same way every other gfx cvar is clamped
  gfx_fExposure       = Clamp(gfx_fExposure,       0.1f, 8.0f);
  gfx_fPostSaturation     = Clamp(gfx_fPostSaturation,     0.0f, 3.0f);
  gfx_fBloomThreshold = Clamp(gfx_fBloomThreshold, 0.0f, 4.0f);
  gfx_fBloomIntensity = Clamp(gfx_fBloomIntensity, 0.0f, 4.0f);
  gfx_iTonemap        = Clamp(gfx_iTonemap,        0L,   1L);

  // Draw straight over the frame, no depth, no blending, full viewport in unit coordinates.
  gfxDisableDepthTest();
  gfxDisableDepthWrite();
  gfxDisableBlend();
  gfxDisableAlphaTest();
  gfxEnableTexture();
  gfxSetOrtho(0.0f, 1.0f, 0.0f, 1.0f, -1.0f, 1.0f, FALSE);
  gfxSetConstantColor(C_WHITE | CT_OPAQUE);

  CaptureInto(_uiSceneTexture, pixW, pixH);

  const PIX pixHalfW = pixW / 2;
  const PIX pixHalfH = pixH / 2;
  const BOOL bBloom = gfx_fBloomIntensity > 0.001f;

  // Occlusion needs the depth buffer and the projection the scene was drawn with. A frame that
  // never set a perspective frustum -- a menu with no world behind it -- has neither.
  const BOOL bAO = gfx_bSSAO && _bDepthUsable && _bPostSceneFrustumValid
                && _uiSsaoProgram != 0 && _fPostSceneFar > _fPostSceneNear;

  if (bAO) {
    gfx_fSSAORadius    = Clamp(gfx_fSSAORadius,    0.05f, 10.0f);
    gfx_fSSAOIntensity = Clamp(gfx_fSSAOIntensity, 0.0f,  4.0f);

    CaptureInto(_uiDepthTexture, pixW, pixH);

    // Occlusion is resolved at half resolution and blurred; the spiral kernel is noisy by design.
    pglViewport(0, 0, pixHalfW, pixHalfH);

    pUseProgram(_uiSsaoProgram);
    SetSampler(_uiSsaoProgram, "texDepth", 0, _uiDepthTexture);
    pUniform1f(pGetUniformLocation(_uiSsaoProgram, "fNear"), _fPostSceneNear);
    pUniform1f(pGetUniformLocation(_uiSsaoProgram, "fFar"), _fPostSceneFar);
    pUniform2f(pGetUniformLocation(_uiSsaoProgram, "vTan"), _fPostSceneTanX, _fPostSceneTanY);
    pUniform1f(pGetUniformLocation(_uiSsaoProgram, "fRadius"), gfx_fSSAORadius);
    pUniform1f(pGetUniformLocation(_uiSsaoProgram, "fIntensity"), gfx_fSSAOIntensity);
    DrawFullScreenQuad();
    CaptureInto(_uiAoA, pixHalfW, pixHalfH);

    pUseProgram(_uiBlurProgram);
    SetSampler(_uiBlurProgram, "texSource", 0, _uiAoA);
    pUniform2f(pGetUniformLocation(_uiBlurProgram, "vDirection"), 1.0f / pixHalfW, 0.0f);
    DrawFullScreenQuad();
    CaptureInto(_uiAoB, pixHalfW, pixHalfH);

    SetSampler(_uiBlurProgram, "texSource", 0, _uiAoB);
    pUniform2f(pGetUniformLocation(_uiBlurProgram, "vDirection"), 0.0f, 1.0f / pixHalfH);
    DrawFullScreenQuad();
    CaptureInto(_uiAoA, pixHalfW, pixHalfH);

    pglViewport(0, 0, pixW, pixH);
  }

  if (bBloom) {
    // Bright pass and blur run at half resolution, in the bottom-left corner of the back buffer.
    pglViewport(0, 0, pixHalfW, pixHalfH);

    pUseProgram(_uiBrightProgram);
    SetSampler(_uiBrightProgram, "texScene", 0, _uiSceneTexture);
    pUniform1f(pGetUniformLocation(_uiBrightProgram, "fThreshold"), gfx_fBloomThreshold);
    DrawFullScreenQuad();
    CaptureInto(_uiBloomA, pixHalfW, pixHalfH);

    pUseProgram(_uiBlurProgram);
    SetSampler(_uiBlurProgram, "texSource", 0, _uiBloomA);
    pUniform2f(pGetUniformLocation(_uiBlurProgram, "vDirection"), 1.0f / pixHalfW, 0.0f);
    DrawFullScreenQuad();
    CaptureInto(_uiBloomB, pixHalfW, pixHalfH);

    SetSampler(_uiBlurProgram, "texSource", 0, _uiBloomB);
    pUniform2f(pGetUniformLocation(_uiBlurProgram, "vDirection"), 0.0f, 1.0f / pixHalfH);
    DrawFullScreenQuad();
    CaptureInto(_uiBloomA, pixHalfW, pixHalfH);

    pglViewport(0, 0, pixW, pixH);
  }

  pUseProgram(_uiCompositeProgram);
  SetSampler(_uiCompositeProgram, "texScene", 0, _uiSceneTexture);
  // With bloom off the second sampler still needs something bound; the unblurred scene is
  // harmless because its intensity is zero.
  SetSampler(_uiCompositeProgram, "texBloom", 1, bBloom ? _uiBloomA : _uiSceneTexture);
  SetSampler(_uiCompositeProgram, "texAO", 2, bAO ? _uiAoA : _uiSceneTexture);
  pUniform1i(pGetUniformLocation(_uiCompositeProgram, "iUseAO"), bAO ? 1 : 0);
  pUniform1f(pGetUniformLocation(_uiCompositeProgram, "fExposure"), gfx_fExposure);
  pUniform1f(pGetUniformLocation(_uiCompositeProgram, "fBloomIntensity"),
             bBloom ? gfx_fBloomIntensity : 0.0f);
  pUniform1f(pGetUniformLocation(_uiCompositeProgram, "fSaturation"), gfx_fPostSaturation);
  pUniform1i(pGetUniformLocation(_uiCompositeProgram, "iTonemap"), gfx_iTonemap);
  gfxSetTextureUnit(0);
  DrawFullScreenQuad();

  if (gfx_bFXAA) {
    // Antialiasing goes last, on the tonemapped image, which is where the edges finally are.
    CaptureInto(_uiWorkTexture, pixW, pixH);
    pUseProgram(_uiFxaaProgram);
    SetSampler(_uiFxaaProgram, "texSource", 0, _uiWorkTexture);
    pUniform2f(pGetUniformLocation(_uiFxaaProgram, "vTexelSize"), 1.0f / pixW, 1.0f / pixH);
    DrawFullScreenQuad();
  }

  // Back to fixed function, and leave the unit the rest of the engine assumes is active.
  pUseProgram(0);
  gfxSetTextureUnit(2);
  pglBindTexture(GL_TEXTURE_2D, 0);
  gfxSetTextureUnit(1);
  pglBindTexture(GL_TEXTURE_2D, 0);
  gfxSetTextureUnit(0);
  pglBindTexture(GL_TEXTURE_2D, 0);
  gfxEnableDepthTest();
  gfxEnableDepthWrite();

  // Consumed. The next frame's world render sets it again; a frame that draws no world leaves it
  // clear, and occlusion correctly sits that frame out rather than reusing a stale projection.
  _bPostSceneFrustumValid = FALSE;
  OGL_CHECKERROR;
}
