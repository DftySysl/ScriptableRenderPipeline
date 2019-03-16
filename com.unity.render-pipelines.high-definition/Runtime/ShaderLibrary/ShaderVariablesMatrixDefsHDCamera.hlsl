#ifdef UNITY_SHADER_VARIABLES_MATRIX_DEFS_LEGACY_UNITY_INCLUDED
    #error Mixing HDCamera and legacy Unity matrix definitions
#endif

#ifndef UNITY_SHADER_VARIABLES_MATRIX_DEFS_HDCAMERA_INCLUDED
#define UNITY_SHADER_VARIABLES_MATRIX_DEFS_HDCAMERA_INCLUDED

#if defined(USING_STEREO_MATRICES)

#define UNITY_MATRIX_V              _ViewConstantsXR[unity_StereoEyeIndex].viewMatrix
#define UNITY_MATRIX_I_V            _ViewConstantsXR[unity_StereoEyeIndex].invViewMatrix
#define UNITY_MATRIX_P              OptimizeProjectionMatrix(_ViewConstantsXR[unity_StereoEyeIndex].projMatrix) // XRTODO: is the optimization valid for XR?
#define UNITY_MATRIX_I_P            _ViewConstantsXR[unity_StereoEyeIndex].invProjMatrix
#define UNITY_MATRIX_VP             _ViewConstantsXR[unity_StereoEyeIndex].viewProjMatrix
#define UNITY_MATRIX_I_VP           _ViewConstantsXR[unity_StereoEyeIndex].invViewProjMatrix
#define UNITY_MATRIX_UNJITTERED_VP  _ViewConstantsXR[unity_StereoEyeIndex].viewProjMatrix //_ViewConstantsXR[unity_StereoEyeIndex].nonJitteredViewProjMatrix
#define UNITY_MATRIX_PREV_VP        _ViewConstantsXR[unity_StereoEyeIndex].prevViewProjMatrix

#else

#define UNITY_MATRIX_V     _ViewMatrix
#define UNITY_MATRIX_I_V   _InvViewMatrix
#define UNITY_MATRIX_P     OptimizeProjectionMatrix(_ProjMatrix)
#define UNITY_MATRIX_I_P   _InvProjMatrix
#define UNITY_MATRIX_VP    _ViewProjMatrix
#define UNITY_MATRIX_I_VP  _InvViewProjMatrix
#define UNITY_MATRIX_UNJITTERED_VP _NonJitteredViewProjMatrix
#define UNITY_MATRIX_PREV_VP _PrevViewProjMatrix

#endif // USING_STEREO_MATRICES

#endif // UNITY_SHADER_VARIABLES_MATRIX_DEFS_HDCAMERA_INCLUDED
