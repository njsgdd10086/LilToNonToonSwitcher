SC_uint(_Enable, 0, [SCInHeader][SCToggle][SCConstValue(1,pixel)], "", "")

SC_Box
SC_Texture2D(_FabricNormalMap, "bump", [], "__NormalMap", "")
SC_float(_FabricNormalStrength, 1, [SCRange(-10,10)], "", "")
SC_float(_FabricStrength, 0.25, [SCRange(0,4)], "", "")
SC_float(_FabricDirX, 0.35, [SCRange(-1,1)], "", "")
SC_float(_FabricDirY, -0.5, [SCRange(-1,1)], "", "")
SC_BoxEnd
