const API = '/api'

export const token = { get: () => localStorage.getItem('osm_token'), set: (value: string) => localStorage.setItem('osm_token', value), clear: () => localStorage.removeItem('osm_token') }

export async function request<T = any>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  if (token.get()) headers.set('Authorization', `Bearer ${token.get()}`)
  if (!(init.body instanceof FormData) && init.body) headers.set('Content-Type', 'application/json')
  const response = await fetch(`${API}${path}`, { ...init, headers })
  if (response.status === 401) { token.clear(); location.reload(); throw new Error('登录已过期') }
  if (!response.ok) { const body = await response.json().catch(() => ({})); throw new Error(body.message || `请求失败 (${response.status})`) }
  return response.status === 204 ? (undefined as T) : response.json()
}

export const post = <T = any>(path: string, body?: unknown) => request<T>(path, { method: 'POST', body: body instanceof FormData ? body : JSON.stringify(body ?? {}) })
export const fmtBytes = (value: number) => value < 1024 ? `${value} B` : value < 1048576 ? `${(value / 1024).toFixed(1)} KB` : value < 1073741824 ? `${(value / 1048576).toFixed(1)} MB` : `${(value / 1073741824).toFixed(1)} GB`
export const fmtTime = (value: string | Date) => new Date(value).toLocaleString('zh-CN', { hour12: false })
