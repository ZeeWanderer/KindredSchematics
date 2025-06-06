﻿using KindredSchematics.Services;
using ProjectM;
using Stunlock.Core;
using System.Text.Json;
using Unity.Entities;

namespace KindredSchematics.ComponentSaver
{
    [ComponentType(typeof(PrisonCell))]
    internal class PrisonCell_Saver : ComponentSaver
    {
        struct PrisonCell_Save
        {
            public PrefabGUID? Buff_PsychicForm { get; set; }
            public AssetGuid? LKey_RequiresPsychicForm { get; set; }
            public AssetGuid? LKey_TargetIsImmune { get; set; }
            public PrefabGUID? ImprisonedBuff { get; set; }
            public int? ImprisonedEntity { get; set; }
        }

        public override object DiffComponents(Entity prefab, Entity entity, EntityMapper entityMapper)
        {
            var prefabData = prefab.Read<PrisonCell>();
            var entityData = entity.Read<PrisonCell>();

            var saveData = new PrisonCell_Save();
            if (!prefabData.Blob.Value.Buff_PsychicForm.Equals(entityData.Blob.Value.Buff_PsychicForm))
                saveData.Buff_PsychicForm = entityData.Blob.Value.Buff_PsychicForm;
            if (prefabData.Blob.Value.LKey_RequiresPsychicForm != entityData.Blob.Value.LKey_RequiresPsychicForm)
                saveData.LKey_RequiresPsychicForm = entityData.Blob.Value.LKey_RequiresPsychicForm;
            if (prefabData.Blob.Value.LKey_TargetIsImmune != entityData.Blob.Value.LKey_TargetIsImmune)
                saveData.LKey_TargetIsImmune = entityData.Blob.Value.LKey_TargetIsImmune;
            if (prefabData.Blob.Value.ImprisonedBuff != entityData.Blob.Value.ImprisonedBuff)
                saveData.ImprisonedBuff = entityData.Blob.Value.ImprisonedBuff;
            if (!prefabData.ImprisonedEntity.Equals(entityData.ImprisonedEntity))
                saveData.ImprisonedEntity = entityMapper.IndexOf(entityData.ImprisonedEntity.GetEntityOnServer());

            if (saveData.Equals(default(PrisonCell_Save)))
                return null;

            return saveData;
        }

        public override object SaveComponent(Entity entity, EntityMapper entityMapper)
        {
            var data = entity.Read<PrisonCell>();
            var saveData = new PrisonCell_Save();
            saveData.Buff_PsychicForm = data.Blob.Value.Buff_PsychicForm;
            saveData.LKey_RequiresPsychicForm = data.Blob.Value.LKey_RequiresPsychicForm;
            saveData.LKey_TargetIsImmune = data.Blob.Value.LKey_TargetIsImmune;
            saveData.ImprisonedBuff = data.Blob.Value.ImprisonedBuff;
            saveData.ImprisonedEntity = entityMapper.IndexOf(data.ImprisonedEntity.GetEntityOnServer());

            return saveData;
        }

        public override void ApplyComponentData(Entity entity, JsonElement jsonData, Entity[] entitiesBeingLoaded)
        {
            var saveData = jsonData.Deserialize<PrisonCell_Save>(SchematicService.GetJsonOptions());

            if (!entity.Has<PrisonCell>())
                entity.Add<PrisonCell>();

            var data = entity.Read<PrisonCell>();

            if (saveData.Buff_PsychicForm != null)
                data.Blob.Value.Buff_PsychicForm = saveData.Buff_PsychicForm.Value;
            if (saveData.LKey_RequiresPsychicForm.HasValue)
                data.Blob.Value.LKey_RequiresPsychicForm = saveData.LKey_RequiresPsychicForm.Value;
            if (saveData.LKey_TargetIsImmune.HasValue)
                data.Blob.Value.LKey_TargetIsImmune = saveData.LKey_TargetIsImmune.Value;
            if (saveData.ImprisonedBuff != null)
                data.Blob.Value.ImprisonedBuff = saveData.ImprisonedBuff.Value;
            if (saveData.ImprisonedEntity != null)
                data.ImprisonedEntity = entitiesBeingLoaded[saveData.ImprisonedEntity.Value];

            entity.Write(data);
        }

        public override int[] GetDependencies(JsonElement data)
        {
            var saveData = data.Deserialize<PrisonCell_Save>(SchematicService.GetJsonOptions());
            if (!saveData.ImprisonedEntity.HasValue)
                return [];
            return [ saveData.ImprisonedEntity.Value ];
        }
    }
}
