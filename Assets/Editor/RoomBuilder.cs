using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.Rendering;

// Turns a ProBuilder shape into a room you can stand inside, with the settings this
// project's movement and lighting actually depend on. Works on any closed shape, not just
// cubes - L-rooms and corridors convert the same way.
//
// USE: make the shape normally (Tools > ProBuilder > Editors > New Shape), size it to the
// room you want, select it, then run Tools > ProBuilder Rooms > Convert Selection To Room.
// Undoable with Ctrl+Z as a single step.
public static class RoomBuilder
{
    // FirstPersonCharacterController.groundMask is layer 6 alone, and Grappling reads the
    // same mask for its obstruction checks. ProBuilder creates meshes on Default, which
    // that mask cannot see at all - so a freshly built room is invisible to the ground
    // check and the player drops straight through it.
    const string GroundLayerName = "Ground";

    [MenuItem("Tools/ProBuilder Rooms/Convert Selection To Room")]
    static void ConvertSelection()
    {
        ProBuilderMesh[] meshes = Selection.GetFiltered<ProBuilderMesh>(SelectionMode.Editable);

        if (meshes.Length == 0)
        {
            EditorUtility.DisplayDialog("Convert To Room",
                "Select one or more ProBuilder shapes first.\n\n" +
                "Make one with Tools > ProBuilder > Editors > New Shape, size it to the " +
                "room you want, then run this.",
                "OK");
            return;
        }

        int groundLayer = LayerMask.NameToLayer(GroundLayerName);

        Undo.SetCurrentGroupName("Convert To Room");
        int group = Undo.GetCurrentGroup();

        foreach (ProBuilderMesh mesh in meshes)
            ConvertToRoom(mesh, groundLayer);

        Undo.CollapseUndoOperations(group);

        string layerNote = groundLayer >= 0
            ? $"on layer '{GroundLayerName}'"
            : $"but layer '{GroundLayerName}' does not exist - set it by hand or the player will fall through";

        EditorUtility.DisplayDialog("Convert To Room",
            $"Converted {meshes.Length} shape(s), {layerNote}.\n\n" +
            "For a doorway: select the wall face, draw the opening with the Cut tool, " +
            "delete that face, then Bridge the edges so the opening has real thickness.\n\n" +
            "Surfaces you want grappleable or wall-runnable need their own layers " +
            "(Grapple Mask / Wall Runnable) - split them off as separate objects.",
            "OK");
    }

    static void ConvertToRoom(ProBuilderMesh mesh, int groundLayer)
    {
        Undo.RecordObject(mesh, "Convert To Room");
        Undo.RecordObject(mesh.gameObject, "Convert To Room");

        // Inward-facing: the player is meant to be standing inside this, so every face
        // has to look back at them.
        foreach (Face face in mesh.faces)
            face.Reverse();

        mesh.ToMesh();
        mesh.Refresh();

        if (groundLayer >= 0)
            SetLayerRecursively(mesh.gameObject, groundLayer);

        if (!mesh.TryGetComponent(out MeshCollider collider))
            collider = Undo.AddComponent<MeshCollider>(mesh.gameObject);
        else
            Undo.RecordObject(collider, "Convert To Room");

        // The convex hull of an inside-out room is just a solid block - leave this on and
        // the player is sealed inside solid geometry. Non-convex is both correct here and
        // collides from either side.
        collider.convex = false;
        if (mesh.TryGetComponent(out MeshFilter filter))
            collider.sharedMesh = filter.sharedMesh;

        if (mesh.TryGetComponent(out MeshRenderer renderer))
        {
            Undo.RecordObject(renderer, "Convert To Room");
            // An inverted hull otherwise lets outside light straight through its
            // backfaces and washes out the interior.
            renderer.shadowCastingMode = ShadowCastingMode.TwoSided;
        }

        GameObjectUtility.SetStaticEditorFlags(mesh.gameObject,
            StaticEditorFlags.ContributeGI |
            StaticEditorFlags.BatchingStatic |
            StaticEditorFlags.OccluderStatic |
            StaticEditorFlags.ReflectionProbeStatic);
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }
}
