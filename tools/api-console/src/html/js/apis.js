// API registry. Add one entry per event API — the sidebar and the page UI
// are generated from this list.
//
// Entry shape:
//   id, name, icon, description, baseUrl
//   auth:     { header: 'Authorization', value: 'Bearer {{API_KEY}}' }
//             or { query: 'api_key' }   (key appended to the URL)
//   endpoints: [{ id, name, method, path, description,
//                 params: [{ name, in: 'path'|'query', label, default, hint }],
//                 body: { ...default JSON body } }]   // body only for POST/PUT/PATCH
//   render(container, ctx)   optional — replaces the generic UI with a custom one
//
// {{API_KEY}} is replaced server-side, so the key never reaches the browser.

const APIS = [
    {
        id: 'vault',
        name: 'Team Credential Vault',
        icon: '◆',
        description: 'Event-issued team credentials (cloud keys, model API keys). Every reveal is audit-logged.',
        baseUrl: 'https://cdao.hackathon.maximus.com/api/teams/7e3a3e89-0e20-42b5-b646-8a857cd51012/credentials',
        auth: { header: 'Authorization', value: 'Bearer {{API_KEY}}' },
        endpoints: [
            {
                id: 'list', name: 'List credentials', method: 'GET', path: '/',
                description: 'Metadata for all active credentials (no secret values).',
            },
            {
                id: 'get', name: 'Get metadata', method: 'GET', path: '/{uuid}/',
                description: 'Metadata for one credential (no secret value).',
                params: [{ name: 'uuid', in: 'path', label: 'Credential UUID', default: '' }],
            },
            {
                id: 'by-name', name: 'Get by name', method: 'GET', path: '/by-name/{name}/',
                description: 'Look up by display name. 409 if the name is not unique.',
                params: [{ name: 'name', in: 'path', label: 'Display name', default: 'AWS Sandbox Key' }],
            },
            {
                id: 'reveal', name: 'Reveal value', method: 'POST', path: '/{uuid}/reveal/',
                description: 'Decrypts and returns the secret. Audit-logged.',
                params: [{ name: 'uuid', in: 'path', label: 'Credential UUID', default: '' }],
            },
            {
                id: 'reveal-by-name', name: 'Reveal by name', method: 'POST', path: '/by-name/{name}/reveal/',
                description: 'Decrypts by display name. 409 if duplicates. Audit-logged.',
                params: [{ name: 'name', in: 'path', label: 'Display name', default: 'AWS Sandbox Key' }],
            },
        ],
    },
    {
        id: 'example',
        name: 'Example (httpbin)',
        icon: '◇',
        description: 'Template entry that echoes requests back. Replace with the event APIs.',
        baseUrl: 'https://httpbin.org',
        auth: { header: 'Authorization', value: 'Bearer {{API_KEY}}' },
        endpoints: [
            {
                id: 'get', name: 'Echo GET', method: 'GET', path: '/get',
                description: 'Returns the query string and headers it received.',
                params: [{ name: 'q', in: 'query', label: 'Query', default: 'hello' }],
            },
            {
                id: 'post', name: 'Echo POST', method: 'POST', path: '/post',
                description: 'Returns the JSON body it received.',
                body: { message: 'hello from CDAO Maximus' },
            },
            {
                id: 'status', name: 'Status code', method: 'GET', path: '/status/{code}',
                description: 'Returns the given HTTP status — handy for testing error logging.',
                params: [{ name: 'code', in: 'path', label: 'Status code', default: '404' }],
            },
        ],
    },
];
