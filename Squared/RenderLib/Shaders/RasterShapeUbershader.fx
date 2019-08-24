// O3 produces literally 1/3 the instructions of OD or O0 so let's just be kind to the driver
#pragma fxcparams(/O3 /Zi)

#define SHAPE_TYPE_NAME RasterShapeUntextured
#define SHAPE_TYPE_NAME_TEX RasterShapeTextured
#define SHAPE_TYPE_TECHNIQUE_NAME RasterShapeTechnique
#define SHAPE_TYPE_TECHNIQUE_NAME_TEX TexturedRasterShapeTechnique
#define SHAPE_TYPE abs(_type.x)

#include "RasterShapeSkeleton.fxh"
#include "RasterShapeImpl.fxh"