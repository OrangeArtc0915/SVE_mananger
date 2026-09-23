import { defineCollection, z } from "astro:content";

const postsCollection = defineCollection({
	schema: z.object({
		title: z.string(),
		published: z.date(),
		updated: z.date().optional(),
		draft: z.boolean().optional().default(false),
		description: z.string().optional().default(""),
		image: z.string().optional().default(""),
		tags: z.array(z.string()).optional().default([]),
		category: z.string().optional().nullable().default(""),
		lang: z.string().optional().default(""),
		pinned: z.boolean().optional().default(false),
		author: z.string().optional().default(""),
		sourceLink: z.string().optional().default(""),
		licenseName: z.string().optional().default(""),
		licenseUrl: z.string().optional().default(""),

		/* Page encryption fields */
		encrypted: z.boolean().optional().default(false),
		password: z.string().optional().default(""),

		

		/* For internal use */
		prevTitle: z.string().default(""),
		prevSlug: z.string().default(""),
		nextTitle: z.string().default(""),
		nextSlug: z.string().default(""),
	}),
});
const specCollection = defineCollection({
	schema: z.object({}),
});
// Wiki（/wiki/）：按 category 分组、按 order 排序
const wikiCollection = defineCollection({
	schema: z.object({
		title: z.string(),
		description: z.string().optional().default(""),
		category: z.string(),
		order: z.number().optional().default(0),
		updated: z.date().optional(),
		// 正文复用别处的文档，写「集合/文件名」，例如 spec/helpus
		ref: z.string().optional(),
	}),
});
export const collections = {
	posts: postsCollection,
	spec: specCollection,
	wiki: wikiCollection,
};
