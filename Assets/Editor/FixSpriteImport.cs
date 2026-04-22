using UnityEditor;
using UnityEngine;

public class FixSpriteImport
{
    public static void Execute()
    {
        string path = "Assets/UI_RateUs_npc01.png";
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
        {
            Debug.LogError("Cannot find texture at " + path);
            return;
        }

        importer.spriteImportMode = SpriteImportMode.Single;
        importer.textureType = TextureImporterType.Sprite;

        TextureImporterSettings settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteMeshType = SpriteMeshType.FullRect;
        settings.spriteExtrude = 4; // Add border padding
        importer.SetTextureSettings(settings);

        importer.SaveAndReimport();
        Debug.Log("Sprite import fixed: MeshType=FullRect, Extrude=4");
    }
}
