import { useEffect, useState } from 'react';
import type { components } from '@shared/contracts/api.generated';
import { apiClient, type ApiAuthContext } from '../../lib/apiClient';

export function BuiltInSkillCatalog({ auth, instanceKey }: { auth: ApiAuthContext | null; instanceKey: string | null }) {
  const [skills, setSkills] = useState<components['schemas']['BuiltInSkillDto'][]>([]);

  useEffect(() => {
    setSkills([]);
    if (!auth) return;

    const controller = new AbortController();
    void apiClient.listBuiltInSkills(auth, controller.signal).then((items) => {
      if (!controller.signal.aborted) setSkills(items);
    }).catch(() => undefined);
    return () => controller.abort();
  }, [auth, instanceKey]);

  if (skills.length === 0) return null;

  return (
    <section aria-labelledby="builtin-skills-heading" className="space-y-2">
      <h3 id="builtin-skills-heading" className="text-sm font-semibold text-slate-100">内置 Skill</h3>
      {skills.map((skill) => (
        <article key={skill.id} className="space-y-1 rounded-lg border border-slate-800 bg-slate-900/65 p-3">
          <h4 className="text-sm font-medium text-slate-100">{skill.title}</h4>
          <p className="text-xs text-slate-300">{skill.description}</p>
          <p className="text-xs text-slate-400">可对助手说「{skill.examplePrompt}」</p>
        </article>
      ))}
    </section>
  );
}
